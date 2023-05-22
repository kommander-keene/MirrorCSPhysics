using System.Collections.Generic;
using UnityEngine;
using Mirror;

public class Controller : NetworkBehaviour
{

    Rigidbody driver;
    private NetworkCCmd netCmdMg;
    public float speed;


    // Replay
    private double replayStart = double.MaxValue;
    public const int MAX_INPUT_QUEUE = 100;
    SortedList<double, InputCmd> InputQueue;
    void Awake()
    {
        InputQueue = new SortedList<double, InputCmd>();
    }
    // Start is called before the first frame update
    bool hasInput()
    {
        return Input.anyKey || Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0;
    }
    void Start()
    {
        driver = GetComponent<Rigidbody>();
        netCmdMg = GetComponent<NetworkCCmd>();
    }

    // Update is called once per frame
    void Update()
    {
        if (isLocalPlayer)
        {
            float AD = Input.GetAxis("Horizontal");
            float WS = Input.GetAxis("Vertical");

            Walk(AD, WS); // Effects Local Client

        }

        if (isLocalPlayer && hasInput())
        {
            float AD = Input.GetAxis("Horizontal") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Horizontal"));
            float WS = Input.GetAxis("Vertical") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Vertical"));

            InputCmd cmd = new InputCmd();
            cmd.axis1 = AD;
            cmd.axis2 = WS;
            netCmdMg.InputDown(cmd);
        }
        else if (!hasInput())
        {
            netCmdMg.InputUp();
        }

    }
    double MostRecentKey()
    {
        return InputQueue.Keys[0];
    }
    InputCmd MostRecentCommand()
    {
        return InputQueue[MostRecentKey()];
    }
    void LateUpdate()
    {
        if (isLocalPlayer && isClient)
        {
            // This command is executed on local clients
            BroadcastLocalInputs();
        }
        if (isServer)
        {
            // Receiving and replicated commands on the server
            if (InputQueue.Count > 0)
            {
                // Pop the first element
                InputCmd cmd = MostRecentCommand();
                double id = MostRecentKey();
                // Set start to current time
                // If we just started receiving actions, then the reference is the earlist start time.
                replayStart = NetworkTime.localTime < replayStart ? NetworkTime.localTime : replayStart;

                // Do the Input() command
                InputReplay(cmd.axis1, cmd.axis2);

                // Check if time elapsed is == the duration of the command
                double elapsed = NetworkTime.localTime - replayStart;
                if (elapsed > cmd.duration)
                {
                    // If so, broadcast Position, Rotation, and Scale back to clients. Include OG timestep.
                    //TODO!
                    TRS_Snapshot snap = new TRS_Snapshot(this.transform.position, this.transform.rotation, this.transform.localScale);
                    ReplyUpdatedPosition(id, snap);
                    replayStart = NetworkTime.localTime; // Set start to begin playing next action
                    InputQueue.RemoveAt(0); // Pop the most recent action. Move onto the next one
                }

            }
            else
            {
                replayStart = double.MaxValue; // Currently, we are not doing anything so forget it.
            }
        }
    }

    void BroadcastLocalInputs()
    {
        // TODO MOVE THIS BACK INTO NETWORK CCmd and do all the stuff there!
        if (NetworkTime.localTime >= netCmdMg.lastServerSendTime + NetworkServer.sendInterval)
        {
            InputCmd toSendCmd = netCmdMg.CurrentCmd();
            bool send = false;
            if (hasInput())
            {

                // Button is currently being held down
                // Chop the command and reset the duration
                netCmdMg.Chop();
                toSendCmd = netCmdMg.CurrentCmd();
                send = true;
                // print($"Input {toSendCmd.axis1} {toSendCmd.axis2} Duration {toSendCmd.duration}");
            }
            else if (netCmdMg.MostRecentCmd())
            {
                send = true;
                // print($"Leftover {toSendCmd.axis1} {toSendCmd.axis2} Duration {toSendCmd.duration}");
            }

            if (send)
            {
                // Send the server command over
                UpdateInputLists(toSendCmd, NetworkTime.time);
            }
        }
    }

    [Command]
    void UpdateInputLists(InputCmd cmd, double id)
    {
        if (InputQueue.Count < MAX_INPUT_QUEUE)
        {
            InputQueue.Add(id, cmd);
        }
    }
    void InputReplay(float AD, float WS)
    {
        // Tell the server I am walking
        Walk(AD, WS);
    }

    [ClientRpc]
    void ReplyUpdatedPosition(double id, TRS_Snapshot snp)
    {
        
    }
    #region shared 
    void Walk(float AD, float WS)
    {
        if (WS != 0)
        {
            driver.AddForce(-driver.transform.up * speed * Mathf.Sign(WS));
        }
        if (AD != 0)
        {
            driver.AddForce(driver.transform.right * speed * Mathf.Sign(AD));
        }
    }
    #endregion
}
