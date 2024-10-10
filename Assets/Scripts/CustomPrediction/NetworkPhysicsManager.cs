using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;

public class NetworkPhysicsManager : MonoBehaviour, INetworkRegistry
{
    public static NetworkPhysicsManager instance;
    private static Scene main;
    private static Scene networked;

    private static PhysicsScene networkedPhysics;
    private static PhysicsScene mainPhysics;
    // TODO Don't use a dictionary
    private Dictionary<uint, GameObject> physicsObjects;
    private uint localID;

    private bool simulateNetwork;
    public GameObject LocalPlayer()
    {
        if (physicsObjects.ContainsKey(localID))
        {
            return null;
        }
        return physicsObjects[localID];
    }
    // Start is called before the first frame update
    void Awake()
    {
        // Create instance example and instantiate frame physics
        if (instance == null)
        {
            instance = this;
            Physics.autoSimulation = false;


            main = SceneManager.CreateScene("MainScene");
            mainPhysics = main.GetPhysicsScene();

            CreateSceneParameters sceneParam = new CreateSceneParameters(LocalPhysicsMode.Physics3D);
            networked = SceneManager.CreateScene("SceneNetworkedPhysics", sceneParam);
            networkedPhysics = networked.GetPhysicsScene();

            physicsObjects = new Dictionary<uint, GameObject>();
            ToggleNetworkSimulation(true); // Always the default!
        }

    }

    void FixedUpdate()
    {
        if (mainPhysics.IsValid())
        {
            mainPhysics.Simulate(Time.fixedDeltaTime);
        }
        if (simulateNetwork && physicsObjects.Count > 0 && networkedPhysics.IsValid())
        {
            networkedPhysics.Simulate(Time.fixedDeltaTime);
        }
    }
    #region Registry Methods
    public bool RegisterNetworkPhysicsObject(uint id, GameObject obj, bool local)
    {
        if (physicsObjects.ContainsKey(id))
        {
            print($"physics.Contains {id}");
            return false;
        }
        physicsObjects.Add(id, obj);
        setNetworkedPhysics(obj);
        return true;
    }
    public bool RemoveNetworkedPhysicsObject(uint id)
    {
        return physicsObjects.Remove(id);
    }

    public void ClearNetworkedPhysics()
    {
        physicsObjects.Clear();
    }

    #endregion

    #region Public Methods
    public void setNetworkedPhysics(GameObject obj)
    {
        SceneManager.MoveGameObjectToScene(obj, networked);
    }

    public void setRegularPhysics(GameObject obj)
    {
        SceneManager.MoveGameObjectToScene(obj, main);
    }

    public void NetworkSimulate(float simulate)
    {
        networkedPhysics.Simulate(simulate);
    }

    public void ToggleNetworkSimulation(bool on)
    {
        simulateNetwork = on;
    }
    public bool IsNetworkSimulationPaused()
    {
        return !simulateNetwork;
    }
    public Dictionary<uint, GameObject> GetPhysicsObjects()
    {
        return physicsObjects;
    }
    public PhysicsScene NetPhysics()
    {
        return networkedPhysics;
    }
    #endregion
}
