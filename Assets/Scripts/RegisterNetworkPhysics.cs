using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RegisterNetworkPhysics : MonoBehaviour
{
    IEnumerator Go()
    {
        yield return new WaitForSeconds(0.1f);
        if (NetworkPhysicsManager.instance != null)
        {
            //Add a new object that is exclusively physics simulated
            bool success = NetworkPhysicsManager.instance.RegisterNetworkPhysicsObject(1000, this.gameObject, false);
            Debug.Assert(success);
        }
    }
    // Start is called before the first frame update
    void Start()
    {
        StartCoroutine(Go());
    }

}
