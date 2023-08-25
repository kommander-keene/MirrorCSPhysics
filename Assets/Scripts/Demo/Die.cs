using UnityEngine;

public class Die : MonoBehaviour
{
    public float delayInSeconds = 3f;

    private void Start()
    {
        Invoke("DestroyParent", delayInSeconds);
    }

    private void DestroyParent()
    {
        Destroy(this.gameObject);
    }
}