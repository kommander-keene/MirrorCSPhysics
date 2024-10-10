using UnityEngine;
using UnityEngine.Analytics;

namespace Mirror
{
    public class CSNetworkRollback : NetworkBehaviour
    {
        [Header("Settings")]
        public double serverStoreRate; // rate at whichs server stores values
        double lastServerStoreTime;
        private ContinuousQueue RollbackQueue; // SERVER: Queue where store rollback events

        #region Rollback
        CSRBSnapshot rbSave; // Saves the state of prior conditions before rollback!
        /// <summary>
        /// Checks to see if system is currently processing a rollback!
        /// </summary>
        /// <returns></returns>
        public bool InRollback()
        {
            return rbSave.Equals(CSRBSnapshot.Null());
        }
        #endregion
        void Awake()
        {
            if (isServer)
            {
                RollbackQueue = new ContinuousQueue();
            }

        }
        private CSRBSnapshot Make()
        {
            return CSRBSnapshot.Create(this.gameObject.transform);
        }
        /// <summary>
        /// Called from the Client -> Server request query to rewind, and include the time sent!
        /// </summary>
        public void Rollback(double queryTime)
        {
            if (isServer) return; // no need to rollback if you are the server

            CSRBSnapshot before = Make();
            // get the rolled back position
            CSRBSnapshot interpolated = RollbackQueue.FindAndInterp(queryTime);
            if (interpolated.Equals(CSRBSnapshot.Null()))
            {
                Debug.LogError("Attempting to Rollback too far into the future");
                return;
            }
            // rewind player to rollback position
            this.transform.position = interpolated.position;
            this.transform.rotation = interpolated.rotation;

            rbSave = before;
        }
        /// <summary>
        /// Reset player back to rollback position
        /// </summary>       
        public void Rollfwd()
        {
            if (!InRollback())
            {
                Debug.LogError("Attempting to roll-forwards without currently rolling back!");
                return;
            }

            this.transform.position = rbSave.position;
            this.transform.rotation = rbSave.rotation;
            rbSave = CSRBSnapshot.Null();
        }

        void FixedUpdate()
        {
            if (isServer)
            {
                if (NetworkTime.localTime >= lastServerStoreTime + serverStoreRate)
                {
                    RollbackQueue.Enqueue(CSRBSnapshot.Create(this.gameObject.transform));
                    lastServerStoreTime = serverStoreRate;
                }

            }
        }
    }

}

