// NetworkTransform V2 by mischa (2021-07)
// comment out the below line to quickly revert the onlySyncOnChange feature
#define onlySyncOnChange_BANDWIDTH_SAVING
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;

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
        /// <summary>
        /// 
        /// </summary>
        Dictionary<uint, TRS_Snapshot> SnapshotMap;


        #endregion
#endif
        public GameObject DebugCube;
        double lastClientSendTime;
        public double lastServerSendTime;

        [Header("Send Interval Multiplier")]
        [Tooltip("Check/Sync every multiple of Network Manager send interval (= 1 / NM Send Rate), instead of every send interval.")]
        [Range(1, 120)]
        const uint sendIntervalMultiplier = 1; // not implemented yet

        [Header("Client Side Interpolation")]
        public bool useMirrorInterpolation; // Only handles transforms and not rigidbodies
        // [Tooltip("Add a small timeline offset to account for decoupled arrival of NetworkTime and NetworkTransform snapshots.\nfixes: https://github.com/MirrorNetworking/Mirror/issues/3427")]
        // public bool timelineOffset = false;
        public int redudancyCapacity;
        public int frameSmoothing;
        public bool predictRotation;
        public float broadcastInterval;
        public float replyInterval;

        // Ninja's Notes on offset & mulitplier:
        // 
        // In a no multiplier scenario:
        // 1. Snapshots are sent every frame (frame being 1 NM send interval).
        // 2. Time Interpolation is set to be 'behind' by 2 frames times.
        // In theory where everything works, we probably have around 2 snapshots before we need to interpolate snapshots. From NT perspective, we should always have around 2 snapshots ready, so no stutter.
        // 
        // In a multiplier scenario:
        // 1. Snapshots are sent every 10 frames.
        // 2. Time Interpolation remains 'behind by 2 frames'.
        // When everything works, we are receiving NT snapshots every 10 frames, but start interpolating after 2. 
        // Even if I assume we had 2 snapshots to begin with to start interpolating (which we don't), by the time we reach 13th frame, we are out of snapshots, and have to wait 7 frames for next snapshot to come. This is the reason why we absolutely need the timestamp adjustment. We are starting way too early to interpolate. 

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
            InputQueue = new Queue<InputGroup>();
            FastIQKeys = new();
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
            return new TRS_Snapshot(target.transform.localPosition, rb.velocity, target.transform.localRotation, rb.angularVelocity);
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
            InputGroup grp = new InputGroup(redudancyCapacity);
            grp.Fill(PreviousInputQueue.CommandArray());
            return grp;
        }
        TRS_Snapshot emergencySnap;
        void RecordMoves(InputCmd cmd)
        {
            cmdCount++;
            if (InputCmd.CmpActions(cmd, currentCmd) && validCmd)
            {
                deltaCmdCount += 1;
                currentCmd.ticks = deltaCmdCount; // Continue counting up!
                // //print("Repeated Command");
                emergencySnap = CreateNewSnapshot(); // Create snapshot after the move
            }
            else if (validCmd && !InputCmd.CmpActions(cmd, currentCmd))
            {

                // //print("Valid Prior");
                // Add old command over
                currentCmd.seq = seqSendUpdate++;
                currentCmd.ticks = deltaCmdCount;
                deltaCmdCount = 1;
                // This is run on the first tick of the new action
                PreviousInputQueue.Enqueue(currentCmd);
                currentCmds.Add(NewInputGrouping());
                uint seqNumber = currentCmd.seq;
                // 
                if (!SnapshotMap.ContainsKey(seqNumber))
                {
                    // //print($"Creating Snapshot At {emergencySnap.position}");
                    SnapshotMap.Add(seqNumber, emergencySnap);
                    // Save the positions in my own lists
                    if (ReplayCommands.Count < numberCommands)
                    {
                        // Increment commands to replay
                        ReplayCommands.Add(currentCmd);
                    }
                }
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
            bool removed = SnapshotMap.Remove(packetID);
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

                    SnapshotMap.Add(emptyCommand.seq, CreateNewSnapshot());
                    if (ReplayCommands.Count < numberCommands)
                    {
                        ReplayCommands.Add(emptyCommand);
                    }
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
                    if (ReplayCommands.Count < numberCommands)
                    {
                        ReplayCommands.Add(currentCmd);
                    }

                    var sn = emergencySnap;
                    if (!SnapshotMap.ContainsKey(currentCmd.seq))
                    {
                        SnapshotMap.Add(currentCmd.seq, sn);
                    }

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

            if (predictRotation)
            {
                beforeR = transform.localRotation;
                beforeAV = trv.angularVelocity;
            }
            // Set to the start

            this.transform.localPosition = lastValid.position;
            trv.velocity = lastValid.velocity;
            if (predictRotation)
            {
                trv.angularVelocity = lastValid.angVel;
                this.transform.localRotation = lastValid.rotation;
            }
            string elements = "";
            foreach (InputCmd command in ReplayCommands)
            {
                elements += command.seq + ", ";
            }
            print($"Start at {id} vs {rewindID}, buffer contains [{elements}]");

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
            NetworkPhysicsManager manager = NetworkPhysicsManager.instance;
            print("ISPAUSED: " + manager.IsNetworkSimulationPaused());
            NetworkPhysicsManager.instance.ToggleNetworkSimulation(false); // stop manual physics network simulation
            int iteration = toRemove;
            if (toRemove < 0)
            {
                yield break; // no point in processing this element
            }

            while (iteration >= 0 && iteration < ReplayCommands.Count)
            {
                // Replay all commands that have not been verified by the server
                InputCmd recent = ReplayCommands[iteration];
                controller.ReplayingInputs(recent);
                // Simulate it forwards by a delta time
                manager.NetworkSimulate(Time.fixedDeltaTime * recent.ticks);
                // GameObject destroy2 = Instantiate(DebugCube, this.transform.localPosition, this.transform.rotation);
                // destroy2.GetComponent<MeshRenderer>().material.color = new Color(1, 0, 0, (iteration + 0.1f) / ReplayCommands.Count);
                // Destroy(destroy2, .5f);
                if (SnapshotMap.ContainsKey(recent.seq))
                {
                    // replace that element with the udpated version
                    TRS_Snapshot replacement = new();
                    // zeroeth-order
                    replacement.position = target.transform.localPosition;
                    replacement.rotation = target.transform.localRotation;
                    // first-order
                    replacement.velocity = trv.velocity;
                    replacement.angVel = trv.angularVelocity;
                    SnapshotMap[recent.seq] = replacement;
                }


                iteration += 1;
            }
            NetworkPhysicsManager.instance.ToggleNetworkSimulation(true);

            if (toRemove == ReplayCommands.Count - 1)
            {
                ReplayCommands.Clear();
                toRemove = -1;
            }
            for (int i = 0; i <= toRemove; i++)
            {
                if (ReplayCommands.Count > 0)
                    ReplayCommands.RemoveAt(0); // remove starting from the front
            }
            rewindID = rewindID < id ? id : rewindID; // store latest processing frame


            Vector3 finalPosition = this.transform.localPosition;
            Vector3 finalVel = trv.velocity;

            Vector3 finalAVB = trv.angularVelocity;
            Quaternion finalRot = transform.localRotation;
            // GameObject destroy2 = Instantiate(DebugCube, lastValid.position, this.transform.rotation);
            // destroy2.GetComponent<MeshRenderer>().material.color = new Color(1, 0, 0, 0.2f);
            // Destroy(destroy2, .1f);

            // destroy2 = Instantiate(DebugCube, finalPosition, finalRot);
            // destroy2.GetComponent<MeshRenderer>().material.color = new Color(0, 1, 0, 0.2f);
            // Destroy(destroy2, .1f);
            if (rewindID > id)
            {
                yield break;
            }
            print("POST CORRECTION ERROR: " + (before - finalPosition).magnitude);
            if ((before - finalPosition).magnitude > instaSnapError)
            {
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
                    print($"Recieved latest: {rewindID} id: " + id.ToString() + $" {mgerror}");
                    DoPositionErrorCorrect(id, serverSnap);
                }
                else
                {
                    //print("Recieved " + id.ToString());
                    // If no errors, means this localSnapshot is okay
                    // Remove this one localSnapshot
                    int target = -1;
                    for (int i = 0; i < ReplayCommands.Count; i++)
                    {
                        if (ReplayCommands[i].seq == id)
                        {
                            target = i;
                            break;
                        }
                    }
                    rewindID = rewindID < id ? id : rewindID;
                    ReplayCommands.RemoveAt(target);
                }


                // Perform an error check, and if not, correct.
                // To correct, just lerp between current position and hypothetical position
            }
            else if (SnapshotMap.Count > 0)
            {
                //     //print("Recieved " + id.ToString()
                // + $"!![{serverSnap.position}] NOT FOUND IN SNAPMAP");
                // Automatically perform corrections
                DoPositionErrorCorrect(id, serverSnap);
            }

            SnapshotMap.Remove(id);
            // Compares the positions and determines whether or not we need to update or not...
            // Need to go change the original changing function
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

        void Start()
        {
            // Always register anything that interacts with the net objects
            if (NetworkPhysicsManager.instance != null)
            {
                //Add a new object that is exclusively physics simulated
                uint netID = target.GetComponent<NetworkIdentity>().netId;
                bool success = NetworkPhysicsManager.instance.RegisterNetworkPhysicsObject(netID, this.gameObject, true);
                // print($"PHYS: Reg player with netID {netID} {success}");
                Debug.Assert(success);
            }
            //Add only myself, since we don't really care where the others are, we are not resimulating them.

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
