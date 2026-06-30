# TODO

## Features

### Interest Handling

- During game logic processing, both client and server should determine peer interests and notify `ReplicationManager`
	when interests change.
- The server should keep a `InterestTable` to track the interests of each peer. This is an
	`Dictionary<int, PeerInterests>`, mapping client peer id to an object that represents the interests of that peer.
- `PeerInterests` is `struct { Requested: ISet<string>, Allowed: ISet<string> }`
	- `Requested` is a set of node groups in which the client has declared interest.
		- A client declaring their own `Requested` list prevents the server from sending state updated before the client
			is ready to receive it. (i.e. if the client is still loading the map, or is in another scene)
	- `Allowed` is a set of node groups to which the server has determined the client should receive replication data.
		- The server determining the `Allowed` list of each peer prevents the malicious clients from receiving more than
			they should.
	- `PeerInterests` also contain a `Consented` list, which is a computed union of the other two, lazily updated when
		either of the other two is changed.
- The server's `ReplicationManager` receives interest updates from local game logic and stores them in the
	`InterestTable` for the corresponding peer, under `Allowed` list.
- The client's `ReplicationManager` receives interest updates from local game logic and reliably sends their own
	interest list to the server. The client doesn't need to keep a `InterestTable`.
- The server's `ReplicationManager` receives interest updates from the clients and stores them in the `InterestTable`
	for the corresponding peer, under `Requested` list.
- If a node group is in both `Requested` and `Allowed`, that means client and server have reached consensus that state
	updates for the nodes in that group should be shared.
- When the `Consented` list changes for a peer, the server finds the `MultiplayerReplicator` nodes of all nodes in the
	new node groups of interest and notifies them so that they mark the objects as dirty for this peer, and send the
	full object's replication data in the next frame.

Issues:
- Interest handling should be optional to simplify things (its awkward to require users to always add replicated nodes
	to a group even if they don't want to use the interest system)
- Sending lists of strings for group names through the internet is inefficient; groups should be integer-indexed
- Currently its awkward to work with dynamic groups

### State Replication

- The authority of a `MultiplayerReplicator` node tracks state change of its parent and notifies the
	`ReplicationManager` of the changed data at every frame.
- The `ReplicationManager` queues replication data from the `MultiplayerReplicator` nodes and unreliably send them at
	the end of each idle frame in a batch. A server `ReplicationManager` broadcasts it to the interested peers; and a
	client sends it to the server, which applies it locally before forwarding to all other interested peers.
- The server `ReplicationManager` must maintain a separate queue for each client. When a server `ReplicationManager`
	sends replication data to clients, it doesn't remove the data from the queue until it receives acknowledgement that
	the client received the data. However, newer queued data overrides current queued data if it's still in the queue.
- Upon receiving replication data, a client `ReplicationManager` queues an acknowledgement to be sent back to the server
	together with the next state sharing message; and notifies the corresponding `MultiplayerReplicator` nodes to update
	the data locally.
- When applying received data locally, a `MultiplayerReplicator` can choose to either apply it immediately or lerp the
	fields from the current value to the new values based on project settings.
	- We could also have an export field in the `MultiplayerReplicator` node to override the project default replication
		behavior for certain fields.

### Scene Spawning

- The `MultiplayerReplicator` node should have an export boolean called `Spawnable`.
- When `MultiplayerReplicator` registers itself on the `ReplicationManager`, if it is `Spawnable`, the manager queues
	a pawn message for every other peer to be sent in the next `ReplicationPacket`.
- The packet should also contain the RID (or another reference) to the scene containing the `MultiplayerReplicator` and
	the path where the scene is being added.
- Upon receiving the packet, each peer instantiates the same scene and add it to the same path.

### // TODO

- Packets should be numbeted and Ack packets must contain the id of the packet to which they are acking so that we don't
	accidentally ack to wrong packets when they arrive out of order; also so that we don't accept old replication data
	that arrives later
