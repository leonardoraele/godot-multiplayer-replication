using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Raele.GodotUtils.Extensions;

namespace Raele.MultiplayerReplication;

public partial class ReplicationManager : Node
{
    // -----------------------------------------------------------------------------------------------------------------
    // STATICS
    // -----------------------------------------------------------------------------------------------------------------

	public const int DEFAULT_PORT = 4000;
	public const string SERVER_BIND_ADDRESS = "*";

    public static ReplicationManager Instance
	{
		get
		{
			if (field == null)
			{
				if (Engine.GetMainLoop() is SceneTree tree)
					field = tree.Root.GetNodeOrNull<ReplicationManager>(nameof(ReplicationManager));
				Debug.Assert(field != null, $"Failed to get {nameof(ReplicationManager)}.{nameof(ReplicationManager.Instance)} because it is not present in the scene tree.");
			}
			return field;
		}
	}

	// -----------------------------------------------------------------------------------------------------------------
	// EXPORTS
	// -----------------------------------------------------------------------------------------------------------------

	// [Export] public

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Maps peer id to peer controller. The controller holds replication information for the peer.
	/// </summary>
	private Dictionary<long, ConnectedPeer> ConnectedPeers = new();

	/// <summary>
	/// Holds a reference to all MultiplayerReplicator nodes in the scene tree, indexed by their unique network id. This
	/// allows for quick lookup of replicators when processing replication data.
	/// </summary>
	private Dictionary<Guid, MultiplayerReplicator> Replicators = new();

	/// <summary>
	/// A preconstructed callable for the <see cref="SendQueuedPackets"/> method. This is useful because this method is
	/// called every frame, and creating a new callable every frame would be inefficient. Instead, we create it once and
	/// reuse it.
	/// </summary>
	// TODO Benchmark this to see if it's actually more efficient than simply calling this.CallDeferred(MethodName.LateProcess).
	private Callable LateProcessCallable => field.Target == null
		? (field = Callable.From(this.LateProcess))
		: field;

	/// <summary>
	/// The interval in milliseconds at which replication data is sent to peers. This is used to control the frequency
	/// of replication updates.
	/// </summary>
	public double TickIntervalSec
	{
		get => field;
		set => field = Mathf.Max(value, Mathf.Epsilon);
	} = 1d/60;

	/// <summary>
	/// The accumulated time since the last replication tick. This is used to determine when to send the next
	/// replication update to peers.
	/// </summary>
	private double TickAccumulatorSec
	{
		get => field;
		set => field = Math.Clamp(value, 0f, this.TickIntervalSec * 2f);
	} = 0f;

    // -----------------------------------------------------------------------------------------------------------------
    // PROPERTIES
    // -----------------------------------------------------------------------------------------------------------------

	public int TickRate
	{
		get => Mathf.RoundToInt(1d / this.TickIntervalSec);
		set => this.TickIntervalSec = 1d / value;
	}

	private ENetMultiplayerPeer? ENetPeer => this.Multiplayer.MultiplayerPeer as ENetMultiplayerPeer;
	private MultiplayerPeer.ConnectionStatus ConnectionStatus => this.Multiplayer.MultiplayerPeer.GetConnectionStatus();

	// -----------------------------------------------------------------------------------------------------------------
	// SIGNALS
	// -----------------------------------------------------------------------------------------------------------------

	[Signal] public delegate void PeerConnectedEventHandler(long peerId);
	[Signal] public delegate void PeerDisconnectedEventHandler(long peerId);
	[Signal] public delegate void ConnectedToServerEventHandler();
	[Signal] public delegate void DisconnectedFromServerEventHandler();
	[Signal] public delegate void ServerStartedEventHandler();
	[Signal] public delegate void ServerStoppedEventHandler();

	/// <summary>
	/// This signal is emitted up to a number of times per second equal to <see cref="TickIntervalSec"/>, but not more
	/// than the idle processing frame rate. It is intended to be used by <see cref="MultiplayerReplicator"/> (and
	/// potentially future other nodes whose processing should be synchronized with the network) to observe properties
	/// and send replication data.
	///
	/// Note that this signal does *not* provide a delta time. Time-based processing should occur in idle or physics
	/// processing. This processing should only be used to send network data.
	///
	/// This signal is emitted in the end of the idle processing phase, after all nodes have been processed for the
	/// current frame. This is to ensure that all nodes have been updated before sending replication data.
	/// </summary>
	[Signal] public delegate void NetworkProcessEventHandler();

    // -----------------------------------------------------------------------------------------------------------------
    // INTERNAL TYPES
    // -----------------------------------------------------------------------------------------------------------------

    // public enum

    // -----------------------------------------------------------------------------------------------------------------
    // EVENTS
    // -----------------------------------------------------------------------------------------------------------------

    public override void _EnterTree()
	{
		base._EnterTree();
		this.Multiplayer.PeerConnected += this.OnPeerConnected;
		this.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
		this.Multiplayer.ConnectedToServer += this.OnConnectedToServer;
		this.Multiplayer.ServerDisconnected += this.OnDisconnectedFromServer;
	}

	public override void _ExitTree()
	{
		base._ExitTree();
		this.Multiplayer.PeerConnected -= this.OnPeerConnected;
		this.Multiplayer.PeerDisconnected -= this.OnPeerDisconnected;
		this.Multiplayer.ConnectedToServer -= this.OnConnectedToServer;
		this.Multiplayer.ServerDisconnected -= this.OnDisconnectedFromServer;
	}

	// public override void _Ready()
	// {
	// 	base._Ready();
	// }

	public override void _Process(double delta)
	{
		base._Process(delta);
		this.LateProcessCallable.CallDeferred();
	}

	// public override void _PhysicsProcess(double delta)
	// {
	// 	base._PhysicsProcess(delta);
	// }

	// public override string[] _GetConfigurationWarnings()
	// 	=> base._PhysicsProcess(delta);

	/// <summary>
	/// We handle network processing in a deferred manner to ensure that all nodes have been processed for the current
	/// frame before we send out replication data. This helps mitigating latency delays that would otherwise occur if we
	/// sent replication data in the beginning of idle frame processing.
	/// </summary>
	private void LateProcess()
	{
		this.TickAccumulatorSec += this.GetProcessDeltaTime();
		if (this.TickAccumulatorSec > this.TickIntervalSec)
		{
			this.TickAccumulatorSec -= this.TickIntervalSec;
			try {
				this.EmitSignal(SignalName.NetworkProcess);
			} finally {
				this.SendQueuedPackets();
			}
		}
	}

	// -----------------------------------------------------------------------------------------------------------------
	// CONNECTIVITY METHODS
	// -----------------------------------------------------------------------------------------------------------------

    public void StartServer(int port = DEFAULT_PORT)
	{
		this.Disconnect();
		ENetMultiplayerPeer peer = new();
		peer.CreateServer(port);
		this.Multiplayer.MultiplayerPeer = peer;
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Server started.", new { port });
		this.EmitSignal(SignalName.ServerStarted);
	}

	public async Task ConnectToServer(string address, int port = DEFAULT_PORT)
	{
		this.Disconnect();
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Connecting to server...", new { address, port });
		ENetMultiplayerPeer peer = new();
		peer.CreateClient(address, port);
		this.Multiplayer.MultiplayerPeer = peer;

		TaskCompletionSource source = new();
		this.Multiplayer.ConnectedToServer += source.SetResult;
		this.Multiplayer.ConnectionFailed += source.SetCanceled;

		try
		{
			await source.Task;
		}
		catch (Exception e)
		{
			GD.PrintErr(nameof(ReplicationManager), " Failed to connect to server. ", e.Message);
			this.DisconnectFromServer();
			throw;
		}
		finally
		{
			this.Multiplayer.ConnectedToServer -= source.SetResult;
			this.Multiplayer.ConnectionFailed -= source.SetCanceled;
		}
	}

    private void OnPeerConnected(long peerId)
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), $"🌐 Peer #{peerId} connected.");
		this.ConnectedPeers[peerId] = new ConnectedPeer(peerId);
		this.EmitSignal(SignalName.PeerConnected, peerId);
	}

	private void OnPeerDisconnected(long peerId)
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), $"🌐 Peer #{peerId} disconnected.");
		this.ConnectedPeers.Remove(peerId);
		this.EmitSignal(SignalName.PeerDisconnected, peerId);
	}

	private void OnConnectedToServer()
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Connected to server.");
		this.EmitSignal(SignalName.ConnectedToServer);
	}

	private void OnDisconnectedFromServer()
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Disconnected from server.");
		this.EmitSignal(SignalName.DisconnectedFromServer);
	}

	public void Disconnect()
	{
		if (
			this.Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer
			|| this.ConnectionStatus == MultiplayerPeer.ConnectionStatus.Disconnected
		)
			return;
		else if (this.Multiplayer.IsServer())
			this.StopMultiplayerServer();
		else
			this.DisconnectFromServer();
	}

	private void StopMultiplayerServer()
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Server stopped.");
		this.BaseDisconnect();
		this.EmitSignal(SignalName.ServerStopped);
	}

	private void DisconnectFromServer()
	{
		GD.PrintS(nameof(ReplicationManager), this.Multiplayer.MultiplayerPeer.GetUniqueId(), "🌐 Disconnected from server.");
		this.BaseDisconnect();
		this.EmitSignal(SignalName.DisconnectedFromServer);
	}

	private void BaseDisconnect()
	{
		this.Multiplayer.MultiplayerPeer?.Close();
		this.Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
		this.ConnectedPeers.Clear();
	}

	// -----------------------------------------------------------------------------------------------------------------
	// INTEREST MANAGEMENT METHODS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Adds a node group to the list of groups that the specified peer is interested in. If this is the server, the
	/// group is added to the peer's permitted interests. If this is a client, the server is notified of the client's
	/// interest so that it can add this new interest to the peer's list of requested interests.
	/// </summary>
	public void AddInterest(long peerId, string group)
	{
		if (this.ConnectionStatus == MultiplayerPeer.ConnectionStatus.Disconnected)
			return;
		if (this.Multiplayer.IsServer())
			if (this.ConnectedPeers.TryGetValue(peerId, out ConnectedPeer? peer))
				peer.Interests.Permitted = new HashSet<string>(peer.Interests.Permitted) { group };
		else
			if (peerId == this.Multiplayer.GetUniqueId())
				this.Rpc(MethodName.RpcAddInterest, group);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer)]
	private void RpcAddInterest(string group)
	{
		long peerId = this.Multiplayer.GetRemoteSenderId();
		Debug.Assert(this.Multiplayer.IsServer(), $"Received {nameof(this.RpcAddInterest)} RPC on a non-server peer.");
		if (this.ConnectedPeers.TryGetValue(peerId, out ConnectedPeer? peer))
			peer.Interests.Requested = new HashSet<string>(peer.Interests.Requested) { group };
	}

	/// <summary>
	/// Removes a node group from the list of groups that the specified peer is interested in. If this is the server,
	/// the group is removed from the peer's permitted interests. If this is a client, the server is notified of the
	/// client's disinterest so that it can remove this interest from the peer's list of requested interests.
	/// </summary>
	public void RemoveInterest(long peerId, string group)
	{
		if (this.Multiplayer.IsServer())
			if (this.ConnectedPeers.TryGetValue(peerId, out ConnectedPeer? peer))
				peer.Interests.Permitted = new HashSet<string>(peer.Interests.Permitted).Except([group]).ToHashSet();
		else
			if (peerId == this.Multiplayer.GetUniqueId())
				this.Rpc(MethodName.RpcRemoveInterest, group);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer)]
	private void RpcRemoveInterest(string group)
	{
		long peerId = this.Multiplayer.GetRemoteSenderId();
		Debug.Assert(this.Multiplayer.IsServer(), $"Received {nameof(this.RpcRemoveInterest)} RPC on a non-server peer.");
		if (this.ConnectedPeers.TryGetValue(peerId, out ConnectedPeer? peer))
			peer.Interests.Requested = new HashSet<string>(peer.Interests.Requested).Except([group]).ToHashSet();
	}

	// -----------------------------------------------------------------------------------------------------------------
	// REPLICATION METHODS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// This method should be called every frame. It sends replication data that has been queued by the
	/// <see cref="MultiplayerReplicator"/>, as well as acknowledgment data for replication data that has been received
	/// from peers.
	/// </summary>
	private void SendQueuedPackets()
	{
		if (this.ConnectionStatus != MultiplayerPeer.ConnectionStatus.Connected)
			return;

		foreach (ConnectedPeer peer in this.ConnectedPeers.Values)
		{
			ReplicationPacket packet = peer.CreateNextReplicationPacket();
			if (!packet.Empty)
				this.RpcId(peer.PeerId, MethodName.RpcAcceptReplicationData, ReplicationPacket.Serialize(packet));
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void RpcAcceptReplicationData(Variant serializedPacket)
	{
		// If we don't have a peer controller for the sender, we can't process the replication data. This can happen if
		// the peer controller was removed due to a disconnect, but the replication packet was still in transit. In this
		// case, we just ignore the replication data.
		if (!this.ConnectedPeers.TryGetValue(this.Multiplayer.GetRemoteSenderId(), out ConnectedPeer? peer))
			return;

		ReplicationPacket packet = ReplicationPacket.Deserialize(serializedPacket);

		// Clear replication data for the acknowledged fields, so that we stop sending the data in the next packets.
		foreach (AckData ack in packet.AckData)
			peer.AcceptAck(ack);

		// Deliver replication data to the appropriate replicators.
		foreach (ReplicationData data in packet.ReplicationData)
		{
			// Enqueue the acknowledgment data for the received replication data, so that we can send it back to the
			// sender in the next replication packet.
			peer.EnqueueAck(new(data.ReplicatorId, data.FieldMask));

			// Ignore packets for replicators that we can't find, but prints a warning.
			if (!this.Replicators.TryGetValue(data.ReplicatorId, out MultiplayerReplicator? replicator))
			{
				GD.PrintRich($"[color=yellow]{nameof(ReplicationManager)} {this.Multiplayer.GetUniqueId()}: Received replication data but could not find the recipient replicator. Replicator ID: \"{data.ReplicatorId}\"");
				continue;
			}

			{
				long expectedSender = this.Multiplayer.IsServer()
					? replicator.GetMultiplayerAuthority()
					: this.GetMultiplayerAuthority();
				if (this.Multiplayer.GetRemoteSenderId() != expectedSender)
				{
					GD.PrintRich($"[color=yellow]{nameof(ReplicationManager)} {this.Multiplayer.GetUniqueId()}: Received replication data from an unexpected sender. Sender ID: {this.Multiplayer.GetRemoteSenderId()}, expected: {expectedSender}");
					continue;
				}
			}

			// Finally deliver the replication data to the replicator.
			replicator.AcceptReplicationData(data);
		}
	}

	public void RegisterReplicator(MultiplayerReplicator replicator)
	{
		this.Replicators[replicator.NetworkId] = replicator;
		replicator.ReplicationData += this.EnqueueReplicationDataForAllPeers;
		replicator.SpawnChild += this.EnqueueSpawnForAllPeers;
	}

	public void UnregisterReplicator(MultiplayerReplicator replicator)
	{
		this.Replicators.Remove(replicator.NetworkId);
		replicator.ReplicationData -= this.EnqueueReplicationDataForAllPeers;
		replicator.SpawnChild -= this.EnqueueSpawnForAllPeers;
	}

	private void EnqueueReplicationDataForAllPeers(ReplicationData data)
	{
		foreach (ConnectedPeer peer in this.ConnectedPeers.Values)
			peer.EnqueueReplicationData(data);
	}

	private void EnqueueSpawnForAllPeers(SpawnData data)
	{
		foreach (ConnectedPeer peer in this.ConnectedPeers.Values)
			peer.EnqueueSpawn(data);
	}

	public TimeSpan GetReplicationInterpolationTime(Node node)
		=> this.GetReplicationInterpolationTime(node.GetMultiplayerAuthority());
	public TimeSpan GetReplicationInterpolationTime(int authority)
	{
		if (this.Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer peer)
			return TimeSpan.Zero;
		double rtt = peer.GetPeer(authority).GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
		return TimeSpan.FromMilliseconds(rtt / 2);
	}
}
