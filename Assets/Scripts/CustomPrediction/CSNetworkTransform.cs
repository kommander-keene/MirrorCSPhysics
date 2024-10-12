// NetworkTransform V2 by mischa (2021-07)
// comment out the below line to quickly revert the onlySyncOnChange feature
#define onlySyncOnChange_BANDWIDTH_SAVING
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Runtime.InteropServices;

namespace Mirror
{
    [AddComponentMenu("Network/Predictive Network Transform")]
    public class CSNetworkTransform : NetworkTransformBase
    {
        // only sync when changed hack /////////////////////////////////////////
#if onlySyncOnChange_BANDWIDTH_SAVING
        [Header("Sync Only If Changed")]
        [Tooltip("When true, changes are not sent unless greater than sensitivity values below.")]
        public bool onlySyncOnChange = true;

        // 3 was original, but testing under really bad network conditions, 2%-5% packet loss and 250-1200ms ping, 5 proved to eliminate any twitching.
        [Tooltip("How much time, as a multiple of send interval, has passed before clearing buffers.")]
        public float bufferResetMultiplier = 5;

        [Header("Sensitivity"), Tooltip("Sensitivity of changes needed before an updated state is sent over the network")]
        public float positionSensitivity = 0.01f;
        public float rotationSensitivity = 0.01f;
        public float scaleSensitivity = 0.01f;

        protected bool positionChanged;
        protected bool rotationChanged;
        protected bool scaleChanged;

        #region PredictionAndReconciliation
        protected TransformSnapshot lastSnapshot;
        protected bool cachedSnapshotComparison;
        protected bool hasSentUnchangedPosition;
        public const int MAX_INPUT_QUEUE = 1024;

        CircularQueueWrapper PreviousInputQueue;
        Queue<InputGroup> InputQueue; // TODO Replace it with a FILO Queue.
        HashSet<uint> FastIQKeys;
        HashSet<uint> KeysToDelete;
        Dictionary<uint, TRS_Snapshot> SnapshotMap;
        // Stores a list of delta maps.
        // Key is associated with the earliest timestamp etc... (0: transition from 0-->1)
        Dictionary<uint, TRS_Snapshot> DeltaMap;


        #endregion
#endif
        double lastClientSendTime;
        public double lastServerSendTime;

        [Header("Client Side Interpolation")]
        public int redudancyCapacity;
        public int frameSmoothing;
        public bool predictRotation;
        public float broadcastInterval;
        public float replyInterval;
        InputCmd currentCmd;
        bool validCmd = false;
        List<InputGroup> currentCmds;
        int numberCommands = 100;
        List<InputCmd> ReplayCommands;
        int deltaCmdCount;
        int cmdCount = 0;
        /// <summary>
        /// Error margin until I start caring about fixing updates
        /// </summary>
        public float errorMargin;
        public IController controller;
        /// <summary>
        /// Correction amount per tick (combine with smoothing!)
        /// </summary>
        public float correctionAmount = .1f;
        /// <summary>
        /// Correction amount per tick for velocities
        /// </summary>
        public float correctionAmountVelocities = 1f;
        /// <summary>
        /// Rate of sending ticks when player is static
        /// </summary>
        public int noInputUpdateRate = 3;
        /// <summary>
        /// How bad does the error have to be before a forceful correction
        /// </summary>
        public float instaSnapError = 1;
        private uint seqSendUpdate;
        private bool down;

        struct DelayReply
        {
            public TRS_Snapshot snapshot;
            public uint mappingID;
            public DelayReply(TRS_Snapshot snp, uint mID)
            {
                mappingID = mID;
                snapshot = snp;
            }
        }
        Queue<DelayReply> DelayReplyQueue;


        private void InitializeLists()
        {
            currentCmds = new List<InputGroup>();
            ReplayCommands = new List<InputCmd>();
            SnapshotMap = new Dictionary<uint, TRS_Snapshot>();
            DeltaMap = new Dictionary<uint, TRS_Snapshot>();
            InputQueue = new Queue<InputGroup>();
            FastIQKeys = new();
            KeysToDelete = new();
            PreviousInputQueue = new CircularQueueWrapper(redudancyCapacity);
            DelayReplyQueue = new Queue<DelayReply>();

        }
        void Awake()
        {
            base.Awake();
            InitializeLists();
        }
        private TRS_Snapshot CreateNewSnapshot()
        {
            Rigidbody rb = target.GetComponent<Rigidbody>();
            TRS_Snapshot snapshot = new(target.transform.localPosition, rb.velocity, target.transform.localRotation, rb.angularVelocity);
            return snapshot;
        }
        public void InputDown(InputCmd cmd)
        {
            RecordMoves(cmd);
            down = true;
        }
        public void InputUp()
        {
            down = false;
        }
        InputGroup NewInputGrouping()
        {
            InputGroup grp = new(redudancyCapacity);
            grp.Fill(PreviousInputQueue.CommandArray());
            return grp;
        }
        TRS_Snapshot newestSnapshot; // store and update delta function between latest Snapshot and Current Position;
        void SaveAndSnap(uint seqNumber, TRS_Snapshot snapshot, InputCmd command)
        {
            if (!SnapshotMap.ContainsKey(seqNumber))
            {
                // Create and save position in the snapshot map
                SnapshotMap.Add(seqNumber, snapshot);
                // Record the difference between position i and i+1
                if (ReplayCommands.Count > 0)
                {
                    uint replayRecent = ReplayCommands[ReplayCommands.Count - 1].seq; // also the early key
                    if (DeltaMap.ContainsKey(replayRecent))
                    {
                        // prevents the system from trying to re-access deleted/processed snapshots
                        return;
                    }
                    TRS_Snapshot early = SnapshotMap[replayRecent];
                    TRS_Snapshot deltaSnapshot = TRS_Snapshot.Delta(early, snapshot);
                    newestSnapshot = snapshot;
                    if (!DeltaMap.ContainsKey(replayRecent))
                    {
                        DeltaMap.Add(replayRecent, deltaSnapshot);
                    }

                    if (seqNumber - 1 != replayRecent)
                    {
                        Debug.LogWarning($"Creating delta between {seqNumber} and {replayRecent}");
                    }

                    // Synchronized Key Deletion
                    if (KeysToDelete.Contains(replayRecent))
                    {
                        print($"REMOVING {replayRecent}");
                        KeysToDelete.Remove(replayRecent);
                        SnapshotMap.Remove(replayRecent);
                    }
                }
                // Save the positions in my own lists
                if (ReplayCommands.Count < numberCommands)
                {
                    // Increment commands to replay
                    ReplayCommands.Add(command);
                }


            }
        }
        TRS_Snapshot emergencySnap;
        void RecordMoves(InputCmd cmd)
        {
            cmdCount++;
            if (InputCmd.CmpActions(cmd, currentCmd) && validCmd)
            {
                deltaCmdCount += 1;
                currentCmd.ticks = deltaCmdCount; // Continue counting up!
                // print("Repeated Command");
                emergencySnap = CreateNewSnapshot(); // Create snapshot after the move
            }
            else if (validCmd && !InputCmd.CmpActions(cmd, currentCmd))
            {

                // print("Valid Prior");
                // Add old command over
                currentCmd.seq = seqSendUpdate++;
                currentCmd.ticks = deltaCmdCount;
                deltaCmdCount = 1;
                // This is run on the first tick of the new action
                PreviousInputQueue.Enqueue(currentCmd);
                currentCmds.Add(NewInputGrouping());
                uint seqNumber = currentCmd.seq;
                SaveAndSnap(seqNumber, emergencySnap, currentCmd);
                currentCmd = cmd; // Update current command to the new command
            }
            else
            {
                // Signals to ignore the previous command.
                deltaCmdCount = 1;
                cmd.ticks = 1;
                currentCmd = cmd;
                // //print("Invalid Prior");
                validCmd = true; // The "list" is now populated with commands
                emergencySnap = CreateNewSnapshot();
            }
        }

        void DelayReplyEnq(uint id, TRS_Snapshot snapshot)
        {
            DelayReply dr = new(snapshot, id);
            DelayReplyQueue.Enqueue(dr);
        }
        void DelayReplyChecker()
        {
            if (NetworkTime.localTime >= lastClientSendTime + replyInterval)
            {
                while (DelayReplyQueue.Count > 0)
                {
                    DelayReply reply = DelayReplyQueue.Dequeue();
                    RpcRecvPosition(reply.mappingID, reply.snapshot);
                }
            }
        }
        public void SetController(IController ctrl)
        {
            controller = ctrl;
        }

        public bool IsReplayingCommands()
        {
            return SnapshotMap.Count == 0 && ReplayCommands.Count == 0 && deltaCmdCount == 0;
        }
        // double timeStampAdjustment => NetworkServer.sendInterval * (sendIntervalMultiplier - 1);
        // double offset => timelineOffset ? NetworkServer.sendInterval * sendIntervalMultiplier : 0;

        protected override void Apply(TransformSnapshot interpolated, TransformSnapshot endGoal)
        {
            // If I am not the local player then I do whatever!
            if (!isLocalPlayer)
            {
                if (syncPosition)
                    target.localPosition = interpolatePosition ? interpolated.position : endGoal.position;

                if (syncRotation)
                    target.localRotation = interpolateRotation ? interpolated.rotation : endGoal.rotation;

                if (syncScale)
                    target.localScale = interpolateScale ? interpolated.scale : endGoal.scale;
            }
        }
        // update //////////////////////////////////////////////////////////////
        // Update applies interpolation
        void Update()
        {

            if (isServer) UpdateServerInterpolation();
            // for all other clients (and for local player if !authority),
            // we need to apply snapshots from the buffer.
            // 'else if' because host mode shouldn't interpolate client
            else if (isClient && !IsClientWithAuthority) UpdateClientInterpolation();
        }
        // LateUpdate broadcasts.
        // movement scripts may change positions in Update.
        // use LateUpdate to ensure changes are detected in the same frame.
        // otherwise this may run before user update, delaying detection until next frame.
        // this could cause visible jitter.

        void LateUpdate()
        {
            // if server then always sync to others.
            if (isServer) UpdateServerBroadcast();
            // client authority, and local player (= allowed to move myself)?
            // 'else if' because host mode shouldn't send anything to server.
            // it is the server. don't overwrite anything there.
            else if (isClient && IsClientWithAuthority) UpdateClientBroadcast();
        }
        int replayed = 0;
        bool toReplay = false;
        int replayed_ticks = 0;

        void FixedUpdate()
        {

            if (isLocalPlayer && isClient && !isServer)
            {
                //BROADCAST
                BroadcastLocalInputs();
            }
            else if (isServer && !isLocalPlayer) // if I am the local player, then I shouldn't be receiving or redoing any commands
            {
                //RECIEVE (SERVER)
                RecieveRemoteCommands();
                DelayReplyChecker();
            }
        }




        [ClientRpc]
        void RpcAckOutOfOrder(uint packetID)
        {
            // Removes Packets which are out of order!
            if (SnapshotMap.ContainsKey(packetID))
            {
                KeysToDelete.Add(packetID);
            }
        }
        uint MostRecentKey()
        {
            return InputQueue.Peek().Recent().seq;
        }
        InputCmd MostRecentCommand()
        {
            return InputQueue.Peek().Recent();
        }
        uint MostRecentKey(int i)
        {
            // periodic error where I try to get the the wrong index??
            return InputQueue.Peek().Get(i).seq;
        }
        InputCmd MostRecentCommand(int i)
        {
            return InputQueue.Peek().Get(i);
        }
        InputCmd server_replayCmd;
        uint serverReplaySID;
        uint lastSeqNumber;
        int interGroupOffset = 0;
        int repeated = 1;
        bool valid;
        void RecieveRemoteCommands()
        {
            bool validSwitch = false;
            if (InputQueue.Count > 0)
            {
                if (!toReplay || !valid)
                {
                    serverReplaySID = MostRecentKey(interGroupOffset);
                    server_replayCmd = MostRecentCommand(interGroupOffset);
                    //print($"{replayed} Setting {serverReplaySID}");
                    // They are different so, it means we havent deducted from replayed ticks yet
                    replayed_ticks = server_replayCmd.ticks;
                    toReplay = true; // Set an id so this doesn't get hit twice
                    if (valid == false)
                    {
                        validSwitch = true;
                    }
                    valid = true; // Force a first iteration
                }
                if (validSwitch && serverReplaySID != 0)
                {
                    // I did not recieve a zero first tick
                    //print($"{replayed} Offset: {lastSeqNumber + 1} is not {MostRecentKey()}");
                    interGroupOffset = (int)serverReplaySID;
                    serverReplaySID = MostRecentKey(interGroupOffset);
                    server_replayCmd = MostRecentCommand(interGroupOffset);
                }
                else if (!validSwitch && interGroupOffset == 0 && lastSeqNumber + 1 != serverReplaySID)
                {
                    // Handles doing offsets
                    //print($"{replayed} Offset: {lastSeqNumber + 1} is not {MostRecentKey()}");
                    uint diff = MostRecentKey() - (lastSeqNumber + 1);
                    interGroupOffset = (int)diff;
                    serverReplaySID = MostRecentKey(interGroupOffset);
                    server_replayCmd = MostRecentCommand(interGroupOffset);
                }

                if (replayed_ticks > 0)
                {
                    controller.ReplayingInputs(server_replayCmd);
                    replayed_ticks -= 1;
                }
                if (replayed_ticks == 0)
                {
                    if (interGroupOffset == 0)
                    {
                        toReplay = false;
                        // Generate new endpoint to send back to client
                        TRS_Snapshot replySnap = CreateNewSnapshot();
                        lastSeqNumber = serverReplaySID; // To keep track of last replayed ID
                        //print($"{replayed} Reply " + serverReplaySID.ToString() + $"({replySnap.position})");
                        DelayReplyEnq(lastSeqNumber, replySnap);
                        InputQueue.Dequeue();
                        FastIQKeys.Remove(serverReplaySID);
                    }
                    else
                    {
                        toReplay = false;
                        // Generate new endpoint to send back to client
                        TRS_Snapshot replySnap = CreateNewSnapshot(); // Create New Snapshot
                        lastSeqNumber = MostRecentKey(interGroupOffset); // Last Replayed ID
                        DelayReplyEnq(lastSeqNumber, replySnap);
                        //print($"{replayed} Reply S " + serverReplaySID.ToString() + $"({replySnap.position})");
                        // Don't remove from the input queue yet
                        interGroupOffset -= 1; // Go back by one step

                    }
                    if (InputQueue.Count > 0 && serverReplaySID < lastSeqNumber)
                    {
                        // Eg. 91 < 93 then its out of order
                        server_replayCmd = MostRecentCommand();
                        serverReplaySID = MostRecentKey();

                        //print($"Out of Order: {serverReplaySID} < {lastSeqNumber}");
                        bool outOfOrder = serverReplaySID < lastSeqNumber;
                        while (InputQueue.Count > 0 && outOfOrder)
                        {
                            server_replayCmd = MostRecentCommand();
                            serverReplaySID = MostRecentKey();
                            InputQueue.Dequeue();
                            FastIQKeys.Remove(serverReplaySID);
                            // Let client know
                            RpcAckOutOfOrder(serverReplaySID);
                        }


                    }
                }
                replayed += 1;
            }
        }
        void BroadcastLocalInputs()
        {
            // Only executed on local clients
            if (NetworkTime.localTime >= lastClientSendTime + broadcastInterval)
            {
                int rcnt = currentCmds.Count;
                while (rcnt > 0)
                {
                    InputGroup rcmd = currentCmds[rcnt - 1];

                    //print("Sent Intermediate: " + rcmd.Recent().seq + $"({rcmd.Recent().ticks}) Rec: " + SnapshotMap[rcmd.Recent().seq].position);
                    CmdUpdateInputLists(rcmd, rcmd.Recent().seq); // Send this to the server
                    rcnt -= 1;
                }
                currentCmds.Clear();
                if (!down && SnapshotMap.Count == 0 && ReplayCommands.Count == 0 && deltaCmdCount == 0 && repeated <= 0) // target.GetComponent<Rigidbody>().velocity == Vector3.zero && 
                {
                    InputCmd emptyCommand = InputCmd.Empty();
                    emptyCommand.seq = seqSendUpdate++;
                    emptyCommand.ticks = 1;

                    PreviousInputQueue.Enqueue(emptyCommand);
                    InputGroup toSendCmd = NewInputGrouping();
                    SaveAndSnap(emptyCommand.seq, CreateNewSnapshot(), emptyCommand);
                    CmdUpdateInputLists(toSendCmd, emptyCommand.seq);
                    repeated = noInputUpdateRate;
                }

                // PROP: Have some external variable keep track of input cmd ticks. Have another keep track of sending between intervals
                else if (deltaCmdCount > 0)
                {
                    currentCmd.seq = seqSendUpdate++;
                    currentCmd.ticks = deltaCmdCount;
                    PreviousInputQueue.Enqueue(currentCmd);
                    InputGroup toSendCmd = NewInputGrouping();
                    // Save the positions in my own lists
                    var sn = emergencySnap;
                    SaveAndSnap(currentCmd.seq, sn, currentCmd);
                    //print("Sent: " + toSendCmd.Recent().seq + $"({toSendCmd.Recent().ticks}) Rec: " + sn.position);
                    CmdUpdateInputLists(toSendCmd, toSendCmd.Recent().seq);

                    deltaCmdCount = 0;
                    validCmd = false;
                }
                repeated -= 1;
                // Clear old snapshots and inputs
                lastClientSendTime = NetworkTime.localTime;
            }
        }

        [Command(channel = Channels.Unreliable)]
        void CmdUpdateInputLists(InputGroup cmd, uint id)
        {

            if (InputQueue.Count < MAX_INPUT_QUEUE && !FastIQKeys.Contains(id))
            {
                InputQueue.Enqueue(cmd);
                FastIQKeys.Add(id);
                Debug.Assert(id == cmd.Recent().seq, $"id was {id} while the most recent command was {cmd.Recent().seq}");
            }
        }
        #region Networked Physics
        private void PrintReplayBuffer(double id)
        {
            string elements = "";
            foreach (InputCmd command in ReplayCommands)
            {
                elements += command.seq + ", ";
            }
            print($"Start at {id} vs {rewindID}, buffer contains [{elements}]");
        }
        private TRS_Snapshot DeltaCurrent()
        {
            // Create a delta function between the current time and latest snapshot

            TRS_Snapshot latest = newestSnapshot;
            return TRS_Snapshot.Delta(latest, CreateNewSnapshot());
        }
        double rewindID;
        /// <summary>
        /// Does the rewinding. Note that values above 10ish start to be really slow to correct
        /// </summary>
        /// <param name="lastValid"></param>
        /// <param name="frame_num"></param>
        /// <returns></returns>
        private IEnumerator Rewind(TRS_Snapshot lastValid, double id)
        {
            int frame_num = frameSmoothing;
            /**
            * Rewind myself to my last valid state
            * Do I have to rewind everything? Maybe.
            */
            if (ReplayCommands.Count == 0)
            {
                // Either nothing to replay
                // or recieved ID has already be thrown away (old)
                yield break;
            }
            // Before times
            var trv = target.GetComponent<Rigidbody>();
            Vector3 before = this.transform.localPosition;
            Vector3 beforeV = trv.velocity;
            Quaternion beforeR = transform.localRotation;
            Vector3 beforeAV = trv.angularVelocity;
            // TODO how do I deal with this the final delta command between the last replay and my current position 
            TRS_Snapshot finalSnapshot = DeltaCurrent();
            // Move the rigidbody to the beginning
            this.transform.localPosition = lastValid.position;
            trv.velocity = lastValid.velocity;
            if (predictRotation)
            {
                trv.angularVelocity = lastValid.angVel;
                this.transform.localRotation = lastValid.rotation;
            }
            // Start movement from last valid positions
            Vector3 finalPosition = lastValid.position;
            Vector3 finalVel = lastValid.velocity;
            Vector3 finalAVB = lastValid.angVel;
            Quaternion finalRot = lastValid.rotation;
            if (ReplayCommands.Count > 1)
            {

                // If ReplayCommand is one no need to replay because that's just the base command
                int toRemove = int.MinValue;
                // In the event I recieve state t1 before t0, 
                // Resimulate from t1, and then remove it alongside everything prior.
                for (int i = 0; i < ReplayCommands.Count; i++)
                {
                    if (ReplayCommands[i].seq == id)
                    {
                        toRemove = i;
                        break;
                    }
                }
                int iteration = toRemove + 1;
                if (toRemove < 0)
                {
                    yield break; // no point in processing this element
                }
                PrintReplayBuffer(id);

                while (iteration >= 0 && iteration < ReplayCommands.Count - 1)
                {
                    // Replay all commands that have not been verified by the server
                    InputCmd recent = ReplayCommands[iteration];
                    TRS_Snapshot currentDelta = DeltaMap[recent.seq];
                    // Step forward simulation using delta functions
                    print($"Rewind {recent.seq}: {currentDelta.position}");
                    finalPosition += currentDelta.position;
                    finalVel += currentDelta.velocity;
                    finalAVB += currentDelta.angVel;
                    finalRot *= currentDelta.rotation;

                    if (SnapshotMap.ContainsKey(recent.seq))
                    {
                        // replace that element with the udpated version
                        TRS_Snapshot replacement = new();
                        // zeroeth-order
                        replacement.position = finalPosition;
                        replacement.rotation = finalRot;
                        // first-order
                        replacement.velocity = finalVel;
                        replacement.angVel = finalAVB;
                        SnapshotMap[recent.seq] = replacement;
                    }

                    iteration += 1;
                }

                if (toRemove == ReplayCommands.Count - 1)
                {
                    ReplayCommands.Clear();
                    DeltaMap.Clear(); // follows ReplayCommands
                    toRemove = -1;
                }
                for (int i = 0; i <= toRemove; i++)
                {
                    if (ReplayCommands.Count > 0)
                    {
                        uint removeID = ReplayCommands[0].seq;
                        if (DeltaMap.ContainsKey(removeID))
                        {
                            DeltaMap.Remove(removeID);
                        }
                        ReplayCommands.RemoveAt(0); // remove starting from the front
                    }
                }

            }
            else
            {
                print("CLEARNING STEP");
                ReplayCommands.Clear();
                DeltaMap.Clear(); // follows ReplayCommands
            }


            print($"Position Difference {finalSnapshot.position}");
            #region Reconcilliation
            rewindID = rewindID < id ? id : rewindID; // store latest processing frame

            if (rewindID > id)
            {
                yield break;
            }
            float snappingError = (before - finalPosition).magnitude;
            if (snappingError > instaSnapError)
            {
                print($"SNAPPING CORRECTION ERROR: Before [{before}] After [{finalPosition}] {snappingError}");
                this.transform.localPosition = finalPosition;
                target.GetComponent<Rigidbody>().velocity = finalVel;
                if (predictRotation)
                {
                    transform.localRotation = finalRot;
                    trv.angularVelocity = finalAVB;
                }
            }
            else
            {
                // Afix this position to before!
                this.transform.localPosition = before;
                trv.velocity = beforeV;
                if (predictRotation)
                {
                    this.transform.localRotation = beforeR;
                    trv.angularVelocity = beforeAV;
                }

                Vector3 error;
                Quaternion errorR;
                int frames = frame_num;
                while (frames > 0)
                {
                    if (rewindID > id)
                    {
                        yield break;
                    }
                    yield return new WaitForEndOfFrame();
                    error = finalPosition - this.transform.localPosition;

                    this.transform.localPosition = Vector3.Lerp(
                        this.transform.localPosition,
                        this.transform.localPosition + error,
                        correctionAmount);

                    target.GetComponent<Rigidbody>().velocity = Vector3.Lerp(
                        target.GetComponent<Rigidbody>().velocity,
                        finalVel,
                        correctionAmountVelocities);
                    if (predictRotation)
                    {
                        errorR = Quaternion.Inverse(this.transform.localRotation) * finalRot;
                        this.transform.localRotation = Quaternion.Slerp(
                            this.transform.localRotation,
                            this.transform.localRotation * errorR,
                            correctionAmount);
                        trv.angularVelocity = Vector3.Lerp(
                            trv.angularVelocity,
                            finalAVB,
                            correctionAmountVelocities);
                    }
                    frames -= 1;
                }
            }
            #endregion
        }
        #endregion
        void DoPositionErrorCorrect(double a, TRS_Snapshot snap)
        {
            if (!isLocalPlayer) return;
            // Run difference through some function that adjusts lerp coefficient based off how different you are
            // this.transform.localPosition = snap.position;
            StartCoroutine(Rewind(snap, a));
        }
        double lastRecievedTime = -1.0;
        [ClientRpc]
        void RpcRecvPosition(uint id, TRS_Snapshot serverSnap)
        {
            if (!isLocalPlayer || isServer) return;
            TRS_Snapshot localSnap;

            if (SnapshotMap.TryGetValue(id, out localSnap))
            {

                // Its in the queue!
                float mgerror = (serverSnap.position - localSnap.position).magnitude;
                bool skip = false;
                if (lastRecievedTime > id)
                {
                    lastRecievedTime = lastRecievedTime > id ? lastRecievedTime : id;
                    skip = true;
                }

                if (!skip && mgerror > errorMargin)
                {
                    // print($"ERROR ON [{id}] My Snapshot {localSnap.position} Server Snapshot {serverSnap.position}");
                    DoPositionErrorCorrect(id, serverSnap);
                }
                else
                {
                    //print("Recieved " + id.ToString());
                    // If no errors, means this localSnapshot is okay
                    // Remove this one localSnapshot
                    int target = -1;
                    uint targetNumber = 0;
                    for (int i = 0; i < ReplayCommands.Count; i++)
                    {
                        if (ReplayCommands[i].seq == id)
                        {
                            target = i;
                            targetNumber = ReplayCommands[i].seq;
                            break;
                        }
                    }
                    rewindID = rewindID < id ? id : rewindID;
                    if (target > 0)
                    {
                        ReplayCommands.RemoveAt(target);
                        // This delta map is being processed and is not used so clear DeltaMap.
                        if (DeltaMap.ContainsKey(targetNumber))
                        {
                            DeltaMap.Remove(targetNumber);
                        }
                    }

                }
                // Perform an error check, and if not, correct.
                // To correct, just lerp between current position and hypothetical position
                KeysToDelete.Add(id);
            }
            else if (SnapshotMap.Count > 0)
            {
                // This is actually an invalid condition...
                // TODO FIX
                DoPositionErrorCorrect(id, serverSnap);
                KeysToDelete.Add(id);
            }
        }

        void UpdateServerBroadcast()
        {
            // broadcast to all clients each 'sendInterval'
            // (client with authority will drop the rpc)
            // NetworkTime.localTime for double precision until Unity has it too
            //
            // IMPORTANT:
            // snapshot interpolation requires constant sending.
            // DO NOT only send if position changed. for example:
            // ---
            // * client sends first position at t=0
            // * ... 10s later ...
            // * client moves again, sends second position at t=10
            // ---
            // * server gets first position at t=0
            // * server gets second position at t=10
            // * server moves from first to second within a time of 10s
            //   => would be a super slow move, instead of a wait & move.
            //
            // IMPORTANT:
            // DO NOT send nulls if not changed 'since last send' either. we
            // send unreliable and don't know which 'last send' the other end
            // received successfully.
            //
            // Checks to ensure server only sends snapshots if object is
            // on server authority(!clientAuthority) mode because on client
            // authority mode snapshots are broadcasted right after the authoritative
            // client updates server in the command function(see above), OR,
            // since host does not send anything to update the server, any client
            // authoritative movement done by the host will have to be broadcasted
            // here by checking IsClientWithAuthority.
            // TODO send same time that NetworkServer sends time snapshot?

            if (NetworkTime.localTime >= lastServerSendTime + NetworkServer.sendInterval && // same interval as time interpolation!
            (syncDirection == SyncDirection.ServerToClient || IsClientWithAuthority))
            {
                // send snapshot without timestamp.
                // receiver gets it from batch timestamp to save bandwidth.
                TransformSnapshot snapshot = Construct();
#if onlySyncOnChange_BANDWIDTH_SAVING
                cachedSnapshotComparison = CompareSnapshots(snapshot);
                if (cachedSnapshotComparison && hasSentUnchangedPosition && onlySyncOnChange) { return; }
#endif

#if onlySyncOnChange_BANDWIDTH_SAVING
                RpcServerToClientSync(
                    // only sync what the user wants to sync
                    syncPosition && positionChanged ? snapshot.position : default(Vector3?),
                    syncRotation && rotationChanged ? snapshot.rotation : default(Quaternion?),
                    syncScale && scaleChanged ? snapshot.scale : default(Vector3?)
                );
#else
RpcServerToClientSync(
    // only sync what the user wants to sync
    syncPosition ? snapshot.position : default(Vector3?),
    syncRotation ? snapshot.rotation : default(Quaternion?),
    syncScale ? snapshot.scale : default(Vector3?)
);
#endif

                lastServerSendTime = NetworkTime.localTime;
#if onlySyncOnChange_BANDWIDTH_SAVING
                if (cachedSnapshotComparison)
                {
                    hasSentUnchangedPosition = true;
                }
                else
                {
                    hasSentUnchangedPosition = false;
                    lastSnapshot = snapshot;
                }
#endif
            }
        }
        void UpdateServerInterpolation()
        {
            // apply buffered snapshots IF client authority
            // -> in server authority, server moves the object
            //    so no need to apply any snapshots there.
            // -> don't apply for host mode player objects either, even if in
            //    client authority mode. if it doesn't go over the network,
            //    then we don't need to do anything.
            // -> connectionToClient is briefly null after scene changes:
            //    https://github.com/MirrorNetworking/Mirror/issues/3329
            if (syncDirection == SyncDirection.ClientToServer &&
            connectionToClient != null &&
            !isOwned)
            {
                if (serverSnapshots.Count == 0) return;

                // step the transform interpolation without touching time.
                // NetworkClient is responsible for time globally.
                SnapshotInterpolation.StepInterpolation(
                    serverSnapshots,
                    connectionToClient.remoteTimeline,
                    out TransformSnapshot from,
                    out TransformSnapshot to,
                    out double t);

                // interpolate & apply
                TransformSnapshot computed = TransformSnapshot.Interpolate(from, to, t);
                Apply(computed, to);
            }
        }
        //IRRELEVAN
        void UpdateClientBroadcast()
        {
            // https://github.com/vis2k/Mirror/pull/2992/
            if (!NetworkClient.ready) return;

            // send to server each 'sendInterval'
            // NetworkTime.localTime for double precision until Unity has it too
            //
            // IMPORTANT:
            // snapshot interpolation requires constant sending.
            // DO NOT only send if position changed. for example:
            // ---
            // * client sends first position at t=0
            // * ... 10s later ...
            // * client moves again, sends second position at t=10
            // ---
            // * server gets first position at t=0
            // * server gets second position at t=10
            // * server moves from first to second within a time of 10s
            //   => would be a super slow move, instead of a wait & move.
            //
            // IMPORTANT:
            // DO NOT send nulls if not changed 'since last send' either. we
            // send unreliable and don't know which 'last send' the other end
            // received successfully.
            if (NetworkTime.localTime >= lastClientSendTime + NetworkClient.sendInterval) // same interval as time interpolation!
            {
                // send snapshot without timestamp.
                // receiver gets it from batch timestamp to save bandwidth.
                TransformSnapshot snapshot = Construct();
#if onlySyncOnChange_BANDWIDTH_SAVING
                cachedSnapshotComparison = CompareSnapshots(snapshot);
                if (cachedSnapshotComparison && hasSentUnchangedPosition && onlySyncOnChange) { return; }
#endif

#if onlySyncOnChange_BANDWIDTH_SAVING
                CmdClientToServerSync(
                    // only sync what the user wants to sync
                    syncPosition && positionChanged ? snapshot.position : default(Vector3?),
                    syncRotation && rotationChanged ? snapshot.rotation : default(Quaternion?),
                    syncScale && scaleChanged ? snapshot.scale : default(Vector3?)
                );
#else
CmdClientToServerSync(
    // only sync what the user wants to sync
    syncPosition ? snapshot.position : default(Vector3?),
    syncRotation ? snapshot.rotation : default(Quaternion?),
    syncScale    ? snapshot.scale    : default(Vector3?)
);
#endif

                lastClientSendTime = NetworkTime.localTime;
#if onlySyncOnChange_BANDWIDTH_SAVING
                if (cachedSnapshotComparison)
                {
                    hasSentUnchangedPosition = true;
                }
                else
                {
                    hasSentUnchangedPosition = false;
                    lastSnapshot = snapshot;
                }
#endif
            }
        }

        void UpdateClientInterpolation()
        {
            // only whileou we have snapshots
            if (clientSnapshots.Count == 0) return;

            // step the interpolation without touching time.
            // NetworkClient is responsible for time globally.
            SnapshotInterpolation.StepInterpolation(
            clientSnapshots,
            NetworkTime.time, // == NetworkClient.localTimeline from snapshot interpolation
            out TransformSnapshot from,
            out TransformSnapshot to,
            out double t);

            // interpolate & apply
            TransformSnapshot computed = TransformSnapshot.Interpolate(from, to, t);
            Apply(computed, to);
        }

        public override void OnSerialize(NetworkWriter writer, bool initialState)
        {
            // sync target component's position on spawn.
            // fixes https://github.com/vis2k/Mirror/pull/3051/
            // (Spawn message wouldn't sync NTChild positions either)
            if (initialState)
            {
                if (syncPosition) writer.WriteVector3(target.localPosition);
                if (syncRotation) writer.WriteQuaternion(target.localRotation);
                if (syncScale) writer.WriteVector3(target.localScale);
            }

        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            // sync target component's position on spawn.
            // fixes https://github.com/vis2k/Mirror/pull/3051/
            // (Spawn message wouldn't sync NTChild positions either)
            if (initialState)
            {
                if (syncPosition) target.localPosition = reader.ReadVector3();
                if (syncRotation) target.localRotation = reader.ReadQuaternion();
                if (syncScale) target.localScale = reader.ReadVector3();
            }
        }


#if onlySyncOnChange_BANDWIDTH_SAVING
        // Returns true if position, rotation AND scale are unchanged, within given sensitivity range.
        protected virtual bool CompareSnapshots(TransformSnapshot currentSnapshot)
        {
            positionChanged = Vector3.SqrMagnitude(lastSnapshot.position - currentSnapshot.position) > positionSensitivity * positionSensitivity;
            rotationChanged = Quaternion.Angle(lastSnapshot.rotation, currentSnapshot.rotation) > rotationSensitivity;
            scaleChanged = Vector3.SqrMagnitude(lastSnapshot.scale - currentSnapshot.scale) > scaleSensitivity * scaleSensitivity;

            return (!positionChanged && !rotationChanged && !scaleChanged);
        }
#endif
        public float timeStampAdjustment;
        public float offset;
        // cmd /////////////////////////////////////////////////////////////////
        // only unreliable. see comment above of this file.

        //IRRELEVANT
        [Command(channel = Channels.Unreliable)]
        void CmdClientToServerSync(Vector3? position, Quaternion? rotation, Vector3? scale)
        {
            OnClientToServerSync(position, rotation, scale);
            //For client authority, immediately pass on the client snapshot to all other
            //clients instead of waiting for server to send its snapshots.
            if (syncDirection == SyncDirection.ClientToServer)
            {
                RpcServerToClientSync(position, rotation, scale);
            }
        }

        // local authority client sends sync message to server for broadcasting
        protected virtual void OnClientToServerSync(Vector3? position, Quaternion? rotation, Vector3? scale)
        {
            // only apply if in client authority mode
            if (syncDirection != SyncDirection.ClientToServer) return;

            // protect against ever growing buffer size attacks
            if (serverSnapshots.Count >= connectionToClient.snapshotBufferSizeLimit) return;

            // only player owned objects (with a connection) can send to
            // server. we can get the timestamp from the connection.
            double timestamp = connectionToClient.remoteTimeStamp;
#if onlySyncOnChange_BANDWIDTH_SAVING
            if (onlySyncOnChange)
            {
                double timeIntervalCheck = bufferResetMultiplier * NetworkClient.sendInterval;

                if (serverSnapshots.Count > 0 && serverSnapshots.Values[serverSnapshots.Count - 1].remoteTime + timeIntervalCheck < timestamp)
                {
                    Reset();
                }
            }
#endif
            AddSnapshot(serverSnapshots, connectionToClient.remoteTimeStamp + timeStampAdjustment + offset, position, rotation, scale);
        }

        // rpc /////////////////////////////////////////////////////////////////
        // only unreliable. see comment above of this file.

        //IRRELEVANT
        [ClientRpc(channel = Channels.Unreliable)]
        void RpcServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale) =>
        OnServerToClientSync(position, rotation, scale);

        // server broadcasts sync message to all clients
        protected virtual void OnServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale)
        {
            // in host mode, the server sends rpcs to all clients.
            // the host client itself will receive them too.
            // -> host server is always the source of truth
            // -> we can ignore any rpc on the host client
            // => otherwise host objects would have ever growing clientBuffers
            // (rpc goes to clients. if isServer is true too then we are host)
            if (isServer) return;

            // don't apply for local player with authority
            if (IsClientWithAuthority) return;

            // on the client, we receive rpcs for all entities.
            // not all of them have a connectionToServer.
            // but all of them go through NetworkClient.connection.
            // we can get the timestamp from there.
            double timestamp = NetworkClient.connection.remoteTimeStamp;
#if onlySyncOnChange_BANDWIDTH_SAVING
            if (onlySyncOnChange)
            {
                double timeIntervalCheck = bufferResetMultiplier * NetworkServer.sendInterval;

                if (clientSnapshots.Count > 0 && clientSnapshots.Values[clientSnapshots.Count - 1].remoteTime + timeIntervalCheck < timestamp)
                {
                    Reset();
                }
            }
#endif
            AddSnapshot(clientSnapshots, NetworkClient.connection.remoteTimeStamp + timeStampAdjustment + offset, position, rotation, scale);
        }
    }
}
