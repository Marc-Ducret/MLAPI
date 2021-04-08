using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;
using MLAPI.Connection;
using MLAPI.Spawning;
using MLAPI.AOI;

// hrm, should we be doing NetworkObject or Object?
namespace MLAPI.EditorTests
{
    public class ClientObjectMapTests
    {
// START HAVING FUN WITH GRID
        public class NaiveRadiusClientObjMapNode : ClientObjMapNode<NetworkClient, NetworkObject>
        {
            public NaiveRadiusClientObjMapNode()
            {
                OnQuery = delegate(in NetworkClient client, HashSet<NetworkObject> results)
                {
                    foreach (var obj in Candidates)
                    {
                        //if (obj == client.PlayerObject) continue;
                        Debug.Log(client.PlayerObject.transform.position + " vs " + obj.transform.position);
                        if (Vector3.Distance(obj.transform.position, client.PlayerObject.transform.position) > 1.5f)
                        {
                            results.Add(obj.GetComponent<NetworkObject>());
                        }
                    }
                };

            }

        }

        public NetworkObject MakeObjectHelper(Vector3 coords, ReplicationGroup rg)
        {
            GameObject o = new GameObject();
            NetworkObject no = (NetworkObject)o.AddComponent(typeof(NetworkObject));
            no.ReplicationGroup = rg;
            no.transform.position = coords;
            return no;
        }

        [Test]
        // Start is called before the first frame update
        public void AOIBasicCheck()
        {
            GameObject o = new GameObject();
            ReplicationGroup rg = ReplicationGroup.Create("test_objects");
// use to test borken            ReplicationGroup rg2 = ReplicationGroup.Create("test_objects2");
            NetworkManager nm = (NetworkManager)o.AddComponent(typeof(NetworkManager));
            nm.SetSingleton();

            HashSet<NetworkObject> objList = new HashSet<NetworkObject>();

            var replicationMgr = new ReplicationManager<NetworkClient, NetworkObject>();
            var naiveRadiusNode = new NaiveRadiusClientObjMapNode();
            replicationMgr.AddNode(naiveRadiusNode, rg);

            // HOORAY, it's broken
            replicationMgr.HandleSpawn(MakeObjectHelper(new Vector3(2.0f, 0.0f, 0.0f), rg));
            replicationMgr.HandleSpawn(MakeObjectHelper(new Vector3(1.0f, 0.0f, 0.0f), rg));
            replicationMgr.HandleSpawn(MakeObjectHelper(new Vector3(3.0f, 0.0f, 0.0f), rg));

            NetworkClient nc = new NetworkClient()
            {
                ClientId = 1,
            };
            nc.PlayerObject = MakeObjectHelper(new Vector3(0.0f,0.0f,0.0f), rg);

            HashSet<NetworkObject> results = new HashSet<NetworkObject>();
            replicationMgr.QueryFor(nc, results);
            int hits = results.Count;
            Debug.Log("there are: " + hits);
            Assert.True(hits == 2);
        }
    }
}
