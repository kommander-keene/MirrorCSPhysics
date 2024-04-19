using System.Collections;
using System.Collections.Generic;
using Mirror;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

public class RegisterNetPhysics : MonoBehaviour
{
    static uint notSimilar = 0;
    uint myID;
    IEnumerator Go()
    {
        yield return new WaitForSeconds(0.1f);
        if (NetworkPhysicsManager.instance != null)
        {
            uint id = (uint)Random.Range(0, 100000);
            uint id_time = (uint)(NetworkTime.time * 100);
            //Add a new object that is exclusively physics simulated
            bool success = NetworkPhysicsManager.instance.RegisterNetworkPhysicsObject(id + notSimilar + id_time, this.gameObject, false);
            notSimilar++;
            Debug.Assert(success);
        }
    }
    // Start is called before the first frame update
    void Start()
    {
        StartCoroutine(Go());
    }


}
