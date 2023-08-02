using System.Collections.Generic;
using UnityEngine;
using Mirror;

public class Controller : NetworkBehaviour, IController
{

    Rigidbody driver;
    public GameObject cubePrefab;
    public GameObject name;
    private NetworkCCmd netCmdMg;
    public float speed;
    // Start is called before the first frame update
    bool hasInput()
    {
        return Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0;
    }
    void Start()
    {
        driver = GetComponent<Rigidbody>();
        netCmdMg = GetComponent<NetworkCCmd>();
        netCmdMg.SetController(this);
        name = GameObject.Find("CubeName");
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        if (isLocalPlayer && hasInput())
        {
            float AD = Input.GetAxis("Horizontal") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Horizontal"));
            float WS = Input.GetAxis("Vertical") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Vertical"));

            InputCmd cmd = new InputCmd();
            cmd.axis1 = AD;
            cmd.axis2 = WS;
            time_run2 += 1;
            Walk(AD, WS); // Effects Local Client
            netCmdMg.InputDown(cmd);

            // print($"{time_run} {time_run2}");
        }
        else if (!hasInput())
        {
            netCmdMg.InputUp();
        }

    }
    public void ReplayingInputs(InputCmd cmd)
    {
        if (isLocalPlayer) return;
        // print($"Trying to replay! {isClient} {isServer} {isLocalPlayer}");
        Walk(cmd.axis1, cmd.axis2);
    }
    int time_run = 0;
    int time_run2 = 0;
    #region shared 
    void Walk(float AD, float WS)
    {
        if (WS != 0)
        {
            driver.AddForce(Vector3.forward * speed * Mathf.Sign(WS), ForceMode.VelocityChange);
            // this.transform.position += -driver.transform.up * speed * Time.fixedDeltaTime * Mathf.Sign(WS);
            // driver.velocity = Vector3.Lerp(-driver.transform.up * speed * Mathf.Sign(WS), driver.velocity, 0.9f);


        }
        if (AD != 0)
        {
            driver.AddForce(Vector3.right * speed * Mathf.Sign(AD), ForceMode.VelocityChange);
            // this.transform.position += driver.transform.right * speed * Time.fixedDeltaTime * Mathf.Sign(AD);
            // driver.velocity = Vector3.Lerp(-driver.transform.right * speed * Mathf.Sign(WS), driver.velocity, 0.9f);
        }
        // if (WS != 0 || AD != 0)
        // {
        //     // Instantiate(cubePrefab, this.transform.position, Quaternion.identity);
        //     name.name = $"Cubes Spawned = {time_run + 1}";
        // }
        time_run = (WS != 0 || AD != 0) ? time_run + 1 : time_run;

    }
    #endregion
}
