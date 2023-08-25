using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public class RegisterNetPhysics : MonoBehaviour
{
    uint myID;
    IEnumerator Go()
    {
        yield return new WaitForSeconds(0.1f);
        if (NetworkPhysicsManager.instance != null)
        {
            uint id = (uint)Random.Range(0, 10000);
            //Add a new object that is exclusively physics simulated
            bool success = NetworkPhysicsManager.instance.RegisterNetworkPhysicsObject(id, this.gameObject, false);

            Debug.Assert(success);
        }
    }
    // Start is called before the first frame update
    void Start()
    {
        StartCoroutine(Go());
    }


}
