// Although this is (currently) inside the MLAPI package, it is intentionally
//  totally decoupled from MLAPI with the intention of allowing it to live
//  in its own package

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;

// every single node has 1 routing group (?)
//  so why not require each node to specify its routing 'thing' at init time
//  and guarantee uniqueness?
//  and this is also where the parent ReplicationGraph owner goes in
//  so long as we don't let you change this after setup (I think)

namespace MLAPI.AOI
{
    [Serializable]
    public class ReplicationGroup // : MonoBehaviour
    // THIS THING ALSO HAS THE SPECIAL REPLICATION SETTINGS
    {
        public static ReplicationGroup Create(string nameIn)
        {
            if (m_allReplicationGroups.ContainsKey("nameIn"))
            {
                throw new ArgumentException("replication group '" + nameIn + "' already exists");
            }
            ReplicationGroup rg = new ReplicationGroup(nameIn);
            m_allReplicationGroups.Add(nameIn, rg);
            return rg;
        }

        private ReplicationGroup(string NameIn)
        {
            Name = NameIn;
        }

        public static ReplicationGroup _Default;
        public static ReplicationGroup Default
        {
            get
            {
                if (_Default == null)
                {
                    _Default = new ReplicationGroup("always_replicate");
                }

                return _Default;
            }

        }

        public string Name;
        private static Dictionary<string, ReplicationGroup> m_allReplicationGroups = new Dictionary<string, ReplicationGroup>();
    }

    public interface IReplicateable
    {
        ReplicationGroup GetReplicationGroup();
    }

    public class ReplicationSettings
    {
        private long m_lastReplTime = 0;
        private int m_priortyScale = 1;
    }

    public class ReplicationManager<TClient, TObject> where TObject : class, IReplicateable
    {
//        public delegate void GetAllObjectsDelegate(HashSet<TObject> results);
//        public GetAllObjectsDelegate GetAllObjects;

        public ReplicationManager()
        {
            m_ChildNodes = new Dictionary<string, ClientObjMapNode<TClient, TObject>>();
            AddNode(new ClientObjMapNodeStatic<TClient, TObject>(), ReplicationGroup.Default);
        }

        public void QueryFor(in TClient client, HashSet<TObject> results)
        {
            if (Bypass)
            {
 ///               GetAllObjects(results);
            }
            else
            {
                foreach (var c in m_ChildNodes.Values)
                {
                    c.QueryFor(client, results);
                }
            }
        }

        // Add a new child node.  Currently, there is no way to remove a node
        public void AddNode(ClientObjMapNode<TClient, TObject> newNode, ReplicationGroup group)
        {
            if (m_ChildNodes.ContainsKey(group.Name))
            {
                throw new ArgumentException("Group with name " + group.Name + " is already registered");
            }
            m_ChildNodes.Add(group.Name, newNode);
        }

        public void HandleSpawn(TObject newNode)
        {
            var rg = newNode.GetReplicationGroup();
            if (rg == null)
            {
                rg = ReplicationGroup.Default;
            } else if (!m_ChildNodes.ContainsKey(rg.Name))
            {
                Debug.LogWarning("Can't find node for replication group '" + rg.Name + "', using default");
                rg = ReplicationGroup.Default;

            }
            m_ChildNodes[rg.Name].Candidates.Add(newNode);
        }

        public void HandleDespawn(TObject oldNode)
        {
 //           m_rootNode.Remove(oldNode);
        }

        public bool Bypass = false;
        public ReplicationSettings GlobalReplicationSettings;

        private Dictionary<string, ClientObjMapNode<TClient, TObject>> m_ChildNodes;
    }


    // To establish a Client Object Map, instantiate a ClientObjMapNodeBase, then
    //  add more nodes to it (and those nodes) as desired
   public class ClientObjMapNode<TClient, TObject> where TObject : class
   {
       // these are the objects under my purview
       //  so if I have a dynamic query, I will check these.
       //  But if I don't have a dynamic query, I will return these (I think)?
        public HashSet<TObject> Candidates;

        // set this delegate if you want a function called when
        //  object 'obj' is being spawned / de-spawned
        public delegate void SpawnDelegate(in TObject obj);
        public SpawnDelegate OnSpawn;
        public SpawnDelegate OnDespawn;

        // to dynamically compute objects to be added each 'QueryFor' call,
        //  assign this delegate to your handler
        public delegate void QueryDelegate(in TClient client, HashSet<TObject> results);
        public QueryDelegate OnQuery;

        public ClientObjMapNode()
        {
            m_ChildNodes = new List<ClientObjMapNode<TClient, TObject>>();
            Candidates = new HashSet<TObject>();
        }

        // externally-called object query function.  Call this on your root
        //  ClientObjectMapNode.  The passed-in hash set will contain the results.
        public void QueryFor(in TClient client, HashSet<TObject> results)
        {
            if (OnQuery != null)
            {
                OnQuery(client, results);
            }

            foreach (var c in m_ChildNodes)
            {
                c.QueryFor(client, results);
            }
        }

        // Called when a given object is about to be despawned.  The OnDespawn
        //  delegate gives each node a chance to do its own handling (e.g. removing
        //  the object from a cache)
        public void HandleSpawn(in TObject obj)
        {
            if (OnSpawn != null)
            {
                OnSpawn(in obj);
            }

            foreach (var c in m_ChildNodes)
            {
                c.HandleSpawn(in obj);
            }
        }

        public void HandleDespawn(in TObject obj)
        {
            if (OnDespawn != null)
            {
                OnDespawn(in obj);
            }

            foreach (var c in m_ChildNodes)
            {
                c.HandleDespawn(in obj);
            }
        }

        // Add a new child node.  Currently, there is no way to remove a node
        public void AddNode(ClientObjMapNode<TClient, TObject> newNode)
        {
            m_ChildNodes.Add(newNode);
        }

        private List<ClientObjMapNode<TClient, TObject>> m_ChildNodes;
        public bool Bypass = false;
   }

   // Static node type.  Objects can be added / removed as desired.
   //  When the Query is done, these objects are grafted in without
   //  any per-object computation.
   public class ClientObjMapNodeStatic<TClient, TObject> : ClientObjMapNode<TClient, TObject> where TObject : class
    {
        public ClientObjMapNodeStatic()
        {
            m_AlwaysRelevant = new HashSet<TObject>();

            // when we are told an object is despawning, remove it from our list
            OnDespawn = Remove;

            // for our query, we simply union our static objects with the results
            //  more sophisticated methods might be explored later, like having the results
            //  list be a list of refs that can be single elements or lists
            OnQuery = (in TClient client, HashSet<TObject> results) => results.UnionWith(m_AlwaysRelevant);
        }

        // Add a new item to our static list
        public void Add(in TObject obj)
        {
            m_AlwaysRelevant.Add(obj);
        }

        public void Remove(in TObject obj)
        {
            m_AlwaysRelevant.Remove(obj);
        }

        private HashSet<TObject> m_AlwaysRelevant;
    }
}
