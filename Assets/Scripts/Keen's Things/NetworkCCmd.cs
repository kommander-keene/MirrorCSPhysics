// NetworkTransform V2 by mischa (2021-07)
// comment out the below line to quickly revert the onlySyncOnChange feature
#define onlySyncOnChange_BANDWIDTH_SAVING
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using Unity.VisualScripting;

namespace Mirror
{
    [AddComponentMenu("Network/Network Command Transform")]
    public class NetworkCCmd : NetworkTransformBase
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

        SortedList<double, InputCmd> InputQueue; // Replace it with a FILO Queue. 
        List<double> TestList = new List<double>();
        int snapshotAge = 0;
        Dictionary<double, TRS_Snapshot> SnapshotMap;

        #endregion
#endif
        public GameObject MirrorPrefab;
        double lastClientSendTime;
        public double lastServerSendTime;

        [Header("Send Interval Multiplier")]
        [Tooltip("Check/Sync every multiple of Network Manager send interval (= 1 / NM Send Rate), instead of every send interval.")]
        [Range(1, 120)]
        const uint sendIntervalMultiplier = 1; // not implemented yet

        [Header("Snapshot Interpolation")]
        // [Tooltip("Add a small timeline offset to account for decoupled arrival of NetworkTime and NetworkTransform snapshots.\nfixes: https://github.com/MirrorNetworking/Mirror/issues/3427")]
        // public bool timelineOffset = false;
        public int frameSmoothing;
        public bool predictRotation;
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
        //
        // Keene
        InputCmd currentCmd;

        List<InputCmd> currentCmds;
        GameObject mirrorPrefab;
        GameObject mirrorClone;
        int numberCommands = 100;
        List<InputCmd> ReplayCommands;
        bool down = false;
        int deltaCmdCount;
        int cmdCount;
        public float errorMargin;
        public IController controller;
        public float correctionAmount = .1f;
        public int noInputUpdateRate = 3;
        public float instaSnapError = 1;
        [Header("Debug")]
        public GameObject CheckerSpawner;
        private void InitializeLists()
        {
            currentCmds = new List<InputCmd>();
            ReplayCommands = new List<InputCmd>();
            SnapshotMap = new Dictionary<double, TRS_Snapshot>();
            InputQueue = new SortedList<double, InputCmd>();
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
            print($"Input at time {Time.time} {CreateNewSnapshot().position}");
            // if (InputCmd.Equals(cmd, currentCmd))
            // {
            // deltaCmdCount += 1;
            // }

            // else
            // {
            //     currentCmd.timestamp = Time.time;
            //     currentCmd.ticks = deltaCmdCount;
            //     if (deltaCmdCount > 0)
            //     {
            //         currentCmds.Add(currentCmd);
            //         double time = currentCmd.timestamp;
            //         if (!SnapshotMap.ContainsKey(time))
            //         {
            //             SnapshotMap.Add(time, CreateNewSnapshot());
            //             // Save the positions in my own lists
            //         }
            //         if (ReplayCommands.Count < numberCommands)
            //         {
            //             ReplayCommands.Add(currentCmd);
            //         }
            //     }
            //     deltaCmdCount = 1;
            //     currentCmd = cmd;
            // }

            deltaCmdCount = 1;
            currentCmd = cmd;

            down = true;
        }
        public void InputUp()
        {
            down = false;
            deltaCmdCount = 0;
        }

        public void SetController(IController ctrl)
        {
            controller = ctrl;
        }
        public InputCmd CurrentCmd()
        {
            return currentCmd;
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
            if (!isLocalPlayer || (GetComponent<Rigidbody>().velocity == Vector3.zero && SnapshotMap.Count == 0 && ReplayCommands.Count == 0))
            {
                Vector3 lerpedInterPosition = Vector3.Slerp(this.transform.localPosition, interpolated.position, 0.1f);
                Vector3 lerpedEndPosition = Vector3.Slerp(this.transform.localPosition, endGoal.position, 0.1f);
                if (syncPosition)
                    target.localPosition = interpolatePosition ? lerpedInterPosition : lerpedEndPosition;

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
        double MostRecentKey()
        {
            int last = InputQueue.Count - 1;
            return InputQueue.Keys[last];
        }
        InputCmd MostRecentCommand()
        {
            return InputQueue[MostRecentKey()];
        }
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

            if (isLocalPlayer && isClient)
            {
                //BROADCAST
                BroadcastLocalInputs();
            }
            else if (isServer && !isLocalPlayer) // if I am the local player, then I shouldn't be receiving or redoing any commands
            {
                //RECIEVE (SERVER)
                RecieveRemoteCommands();
            }
        }



        InputCmd server_replayCmd;
        double server_replayID;
        double lastReplayedTime;

        int repeated = 1;
        Vector3 remember;
        void RecieveRemoteCommands()
        {
            if (!toReplay && InputQueue.Count > 0)
            {
                server_replayID = MostRecentKey();
                server_replayCmd = MostRecentCommand();
                // They are different so, it means we havent deducted from replayed ticks yet
                replayed_ticks = server_replayCmd.ticks;
                toReplay = true; // Set an id so this doesn't get hit twice
            }
            // RECIEVING AND REPLAYING COMMANDS FROM SERVER!!!
            if (InputQueue.Count > 0)
            {
                // TODO still a minor bug where extremely button presses will cause mismatch.
                // Extremely hard to reproduce, so ignore this for now. :(

                if (replayed_ticks > 0)
                {
                    server_replayID = MostRecentKey();
                    server_replayCmd = MostRecentCommand();
                    // This is just incase the command changes or something due to the sorts
                    print($"Server Before Position = {transform.localPosition}");
                    controller.ReplayingInputs(server_replayCmd);

                    replayed_ticks -= 1;
                    replayed += 1;
                }
                else
                {

                    toReplay = false;
                    // Generate new endpoint to send back to client
                    TRS_Snapshot replySnap = CreateNewSnapshot();
                    print($"Server After Position = {replySnap.position}");
                    lastReplayedTime = server_replayID; // To keep track of last replayed ID
                                                        // print("Sent ServerID is " + server_replayID);
                    remember = transform.position;
                    RpcRecvPosition(server_replayID, replySnap);
                    InputQueue.Remove(server_replayID); // Pop the most recent action. Move onto the next one
                                                        // Pop next one
                    if (InputQueue.Count > 0)
                    {
                        server_replayCmd = MostRecentCommand();
                        server_replayID = MostRecentKey();

                        // bool outOfOrder = server_replayID < lastReplayedTime;

                        // while (InputQueue.Count > 0 && outOfOrder)
                        // {
                        //     server_replayCmd = MostRecentCommand();
                        //     server_replayID = MostRecentKey();
                        //     InputQueue.Remove(server_replayID);
                        // }

                    }
                }



            }
        }
        void BroadcastLocalInputs()
        {
            // Only executed on local clients
            if (NetworkTime.localTime >= lastClientSendTime + NetworkClient.sendInterval)
            {
                int rcnt = currentCmds.Count;

                while (rcnt > 0)
                {
                    InputCmd rcmd = currentCmds[rcnt - 1];
                    CmdUpdateInputLists(rcmd, rcmd.timestamp);
                    rcnt -= 1;
                }
                currentCmds.Clear();
                // Send redundant inputs over

                InputCmd toSendCmd = CurrentCmd();
                if (SnapshotMap.Count == 0 && ReplayCommands.Count == 0 && deltaCmdCount == 0 && repeated <= 0) // target.GetComponent<Rigidbody>().velocity == Vector3.zero && 
                {
                    InputCmd emptyCmd = InputCmd.Empty(); // create and return a new completely empty command
                    double time = Time.time;
                    emptyCmd.ticks = 1;

                    SnapshotMap.Add(time, CreateNewSnapshot());

                    if (ReplayCommands.Count < numberCommands)
                    {
                        ReplayCommands.Add(emptyCmd);
                    }
                    CmdUpdateInputLists(toSendCmd, time);
                    repeated = noInputUpdateRate;
                }
                // PROP: Have some external variable keep track of input cmd ticks. Have another keep track of sending between intervals
                else if (deltaCmdCount > 0)
                {
                    double time = Time.time;
                    if (!SnapshotMap.ContainsKey(time))
                    {
                        var sn = CreateNewSnapshot();
                        SnapshotMap.Add(time, sn);
                        print("SENDING " + sn.position);
                    }
                    else
                    {
                        SnapshotMap[time] = CreateNewSnapshot();
                        print("SENDING 2" + SnapshotMap[time].position);
                    }
                    // Send the server command over (according to this the number of send commands should be accurate now)
                    toSendCmd.ticks = deltaCmdCount;
                    // Save the positions in my own lists
                    if (ReplayCommands.Count < numberCommands)
                    {
                        ReplayCommands.Add(toSendCmd);
                    }
                    CmdUpdateInputLists(toSendCmd, time);
                    deltaCmdCount = 0;
                }
                repeated -= 1;
                // Clear old snapshots and inputs
                lastClientSendTime = NetworkTime.localTime;
            }
        }

        [Command(channel = Channels.Unreliable)]
        void CmdUpdateInputLists(InputCmd cmd, double id)
        {

            if (InputQueue.Count < MAX_INPUT_QUEUE && !InputQueue.ContainsKey(id))
            {
                InputQueue.Add(id, cmd);
            }


            // FIGURED IT OUT! InputQueue seems to randomly be dropping some of my ids for some reason?
            // print($"{this.gameObject.name} is adding a command to the input list");
            // print($"SERVER: Adding command to input queue with id {id} {InputQueue.Count}");


        }
        #region Networked Physics
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
                // Oopsies nothing to replay haha
                yield return null;
            }
            else
            {
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

                NetworkPhysicsManager.instance.ToggleNetworkSimulation(false); // stop manual physics network simulation

                NetworkPhysicsManager manager = NetworkPhysicsManager.instance;
                int iteration = ReplayCommands.Count - 1;
                while (iteration >= 0)
                {
                    InputCmd recent = ReplayCommands[iteration]; // Fetch the most recent ReplayCommand
                    controller.ReplayingInputs(recent);
                    // Simulate it forwards by a delta time
                    manager.NetworkSimulate(Time.fixedDeltaTime * recent.ticks);
                    iteration -= 1;
                }
                ReplayCommands.Clear();
                NetworkPhysicsManager.instance.ToggleNetworkSimulation(true);

                Vector3 finalPosition = this.transform.localPosition;
                Vector3 finalVel = trv.velocity;

                Vector3 finalAVB = trv.angularVelocity;
                Quaternion finalRot = transform.localRotation;

                if ((before - finalPosition).magnitude > instaSnapError)
                {
                    print($"{id} At {before} and near {finalPosition}");
                    this.transform.localPosition = finalPosition;
                    target.GetComponent<Rigidbody>().velocity = finalVel;
                    if (predictRotation)
                    {
                        transform.localRotation = finalRot;
                        trv.angularVelocity = finalAVB;
                    }

                    Instantiate(CheckerSpawner, finalPosition, finalRot);
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
                        // float frameCNT = (float)(frame_num - frames) / (float)frame_num;

                        // Position and Velocity
                        // print($"Soft Correction {finalPosition - this.transform.localPosition}");
                        error = finalPosition - this.transform.localPosition;
                        this.transform.localPosition = Vector3.Lerp(this.transform.localPosition, this.transform.localPosition + error, correctionAmount);
                        target.GetComponent<Rigidbody>().velocity = Vector3.Lerp(target.GetComponent<Rigidbody>().velocity, finalVel, 1);


                        if (predictRotation)
                        {
                            errorR = Quaternion.Inverse(this.transform.localRotation) * finalRot;
                            this.transform.localRotation = Quaternion.Lerp(this.transform.localRotation, this.transform.localRotation * errorR, correctionAmount);
                            trv.angularVelocity = Vector3.Lerp(trv.angularVelocity, finalAVB, 1);
                        }
                        frames -= 1;
                        yield return new WaitForFixedUpdate();
                    }
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
        [ClientRpc(channel = Channels.Unreliable)]
        void RpcRecvPosition(double id, TRS_Snapshot serverSnap)
        {
            if (!isLocalPlayer || !isClient) return;

            TRS_Snapshot localSnap;

            if (SnapshotMap.TryGetValue(id, out localSnap))
            {
                // print($"SERVER POSITION: {sv_snp.position}");

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
                    print($"RECV: {id} I am at {localSnap.position} vs server is at {serverSnap.position}");
                    DoPositionErrorCorrect(id, serverSnap);
                    bool removed = SnapshotMap.Remove(id);
                    // Remove from list!
                }
                else
                {
                    ReplayCommands.Clear();
                }


                // Perform an error check, and if not, correct.
                // To correct, just lerp between current position and hypothetical position
            }
            else if (SnapshotMap.Count > 0)
            {
                print("Error Something is not quiet right!");
                // Automatically perform corrections
                DoPositionErrorCorrect(id, serverSnap);
            }

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
