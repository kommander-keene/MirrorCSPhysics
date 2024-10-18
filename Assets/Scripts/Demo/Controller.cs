using System.Collections;
using UnityEngine;
using Mirror;

public class Controller : NetworkBehaviour, IController
{

    Rigidbody driver;
    public bool determinism;
    public bool forces;
    public GameObject cubePrefab;
    public GameObject name;
    private CSNetworkTransform netCmdMg;
    public float speed;
    // Start is called before the first frame update
    bool hasInput()
    {
        return Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0 || Input.GetAxis("Jump") != 0;
    }
    void Start()
    {
        driver = GetComponent<Rigidbody>();
        netCmdMg = GetComponent<CSNetworkTransform>();
        netCmdMg.SetController(this);
        name = GameObject.Find("CubeName");
    }
    IEnumerator Timer()
    {
        yield return new WaitForFixedUpdate();

        while (MSPassed < 30)
        {
            MSPassed += 1;
            yield return new WaitForFixedUpdate();
        }
    }
    int MSPassed = 30;
    void FixedUpdate()
    {
        if (isLocalPlayer && hasInput())
        {
            float AD = Input.GetAxis("Horizontal") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Horizontal"));
            float WS = Input.GetAxis("Vertical") == 0 ? 0 : Mathf.Sign(Input.GetAxis("Vertical"));
            float J = Input.GetAxisRaw("Jump");
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
    #region shared 
    void Walk(float AD, float WS, float J)
    {
        Vector2 direction = new Vector2(AD, WS);
        if (!determinism)
        {
            if (!forces)
                driver.velocity = Vector3.forward * speed * direction.y + Vector3.right * speed * direction.x;
            else
                driver.AddForce(Vector3.forward * speed / 25 * direction.y + Vector3.right * speed / 25 * direction.x, ForceMode.VelocityChange);
        }
        else
        {
            print($"Z1: PRE MOVEMENT! {transform.position}");
            transform.position += Time.fixedDeltaTime * (Vector3.forward * speed * direction.y + Vector3.right * speed * direction.x);
            print($"Z2: APPLYING MOVEMENT! {transform.position}");
        }

    }
    #endregion
}
