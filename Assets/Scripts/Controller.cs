using System.Collections.Generic;
using System.Collections;
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
        return Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0 || Input.GetAxis("Jump") != 0;
    }
    void Start()
    {
        driver = GetComponent<Rigidbody>();
        netCmdMg = GetComponent<NetworkCCmd>();
        netCmdMg.SetController(this);
        name = GameObject.Find("CubeName");
    }
    bool jumperonied = true;
    // Update is called once per frame
    void FixedUpdate()
    {
        if (isLocalPlayer && hasInput())
        {
            float AD = Input.GetAxis("Horizontal") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Horizontal"));
            float WS = Input.GetAxis("Vertical") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Vertical"));
            float J = Input.GetAxis("Jump");
            InputCmd cmd = new InputCmd();
            cmd.axis1 = AD;
            cmd.axis2 = WS;
            cmd.axis3 = J;
            time_run2 += 1;
            Walk(AD, WS, J); // Effects Local Client
            netCmdMg.InputDown(cmd);
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
        Walk(cmd.axis1, cmd.axis2, cmd.axis3);
    }
    int time_run = 0;
    int time_run2 = 0;

    IEnumerator balls()
    {
        yield return new WaitForSeconds(0.5f);
        // jumperonied = true;
        pressed = true;
    }
    bool pressed = true;
    #region shared 
    void Walk(float AD, float WS, float J)
    {
        print($"Pre-move {this.transform.localPosition}");
        if (WS != 0 && pressed)
        {
            // driver.AddForce(Vector3.forward * speed * Mathf.Sign(WS), ForceMode.VelocityChange);
            // this.transform.position += -driver.transform.up * speed * Time.fixedDeltaTime * Mathf.Sign(WS);
            // driver.velocity = Vector3.Lerp(-driver.transform.up * speed * Mathf.Sign(WS), driver.velocity, 0.9f);
            driver.transform.localPosition += Vector3.forward * .5f * Mathf.Sign(WS);
            // StartCoroutine(balls());
            // pressed = false;
        }
        print($"Post-move {this.transform.localPosition}");
        // if (AD != 0)
        // {
        //     driver.position = Vector3.right * 10f * Mathf.Sign(AD);
        //     // driver.AddForce(Vector3.right * speed * Mathf.Sign(AD), ForceMode.VelocityChange);
        //     // this.transform.position += driver.transform.right * speed * Time.fixedDeltaTime * Mathf.Sign(AD);
        //     // driver.velocity = Vector3.Lerp(-driver.transform.right * speed * Mathf.Sign(WS), driver.velocity, 0.9f);
        // }
        // if (J != 0 && jumperonied)
        // {
        //     // driver.AddForce(Vector3.up * 20, ForceMode.VelocityChange);
        //     driver.position = Vector3.up * 3.30f;
        //     // jumperonied = false;
        //     // StartCoroutine(balls());
        // }
        // if (WS != 0 || AD != 0)
        // {
        //     // Instantiate(cubePrefab, this.transform.position, Quaternion.identity);
        //     name.name = $"Cubes Spawned = {time_run + 1}";
        // }
        // time_run = (WS != 0 || AD != 0) ? time_run + 1 : time_run;

    }
    #endregion
}
