
using UnityEngine;
// Manually helps to register different networking objects
public interface INetworkRegistry
{
    // Register Networked Physics Object into the system
    public bool RegisterNetworkPhysicsObject(uint id, GameObject obj, bool localHost);
    // Remove Networked Physics Object from the system
    public GameObject RemoveNetworkedPhysicsObject(uint id);
}