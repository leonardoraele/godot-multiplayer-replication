using System;
using System.Collections.Generic;
using System.Linq;

namespace Raele.MultiplayerReplication;

/// <summary>
/// Holds replication information related to a single peer, including its interests and replication queue.
/// </summary>
public class ConnectedPeer
{
	public ConnectedPeer(long peerId) => this.PeerId = peerId;

	/// <summary>
	/// The unique identifier for the peer.
	/// </summary>
	public readonly long PeerId;

	/// <summary>
	/// The interests of the peer, which determine what data it should receive.
	/// </summary>
	public readonly PeerInterests Interests = new();

	/// <summary>
	/// The queue of replication data awaiting to be sent to the peer.
	/// </summary>
	public readonly Dictionary<Guid, ReplicationData> ReplicationQueue = [];

	/// <summary>
	/// The queue of acknowledgments waiting to be sent to authority peers.
	/// </summary>
	public readonly Dictionary<Guid, AckData> AckQueue = [];

	/// <summary>
	/// Adds replication data to the queue to be sent to the peer in the next replication packet. If there is already
	/// replication data for the same replicator ID, the new replication data is merged with the existing one.
	/// </summary>
	public void EnqueueReplicationData(ReplicationData newData)
	{
		if (this.ReplicationQueue.TryGetValue(newData.ReplicatorId, out ReplicationData? currentData))
			currentData.MergeInplace(newData.FieldMask, newData.Values);
		else
			this.ReplicationQueue[newData.ReplicatorId] = newData;
	}

	/// <summary>
	/// Adds acknowledgment data to the queue to be send to the peer in the next replication packet. If there is already
	/// acknowledgment data for the same replicator ID, the new acknowledgment data is merged with the existing one.
	///
	/// This method should be called by the ReplicationManager when it receives replication data from the peer, so that
	/// we enqueue acknowledgement data to send back to in the next replication packet.
	/// </summary>
	/// <param name="ackData"></param>
	public void EnqueueAck(AckData ackData)
	{
		this.AckQueue[ackData.ReplicatorId] = this.AckQueue.GetValueOrDefault(ackData.ReplicatorId)?.Union(ackData)
			?? ackData;
	}

	/// <summary>
	/// Removes replication data from the queue for the specified replicator ID and field mask. This method should be
	/// called by the ReplicationManager when it receives acknowledgment data from the peer, so that we stop sending
	/// replication data for the specified replicator fields.
	/// </summary>
	public void AcceptAck(AckData ack)
	{
		if (
			this.ReplicationQueue.TryGetValue(ack.ReplicatorId, out ReplicationData? data)
			&& data.AckInplace(ack.FieldMask)
		)
			this.ReplicationQueue.Remove(ack.ReplicatorId);
	}

	// /// <summary>
	// /// Removes acknowledgment data from the queue for the specified replicator ID and field mask. This method should be
	// /// called by the ReplicationManager when it receives acknowledgment data from the peer, so that we stop sending
	// /// acknowledgement for the specified replicator fields.
	// /// </summary>
	// public void ClearAck(Guid replicatorId, uint fieldMask)
	// {
	// 	if (!this.AckQueue.TryGetValue(replicatorId, out AckData? ackData))
	// 		return;
	// 	AckData ackDiff = ackData.Difference(fieldMask);
	// 	if (ackDiff.Empty)
	// 		this.AckQueue.Remove(replicatorId);
	// 	else
	// 		this.AckQueue[replicatorId] = ackDiff;
	// }

	/// <summary>
	/// Creates a replication packet containing the replication data and acknowledgment data queued to be sent to this
	/// peer.
	/// </summary>
	public ReplicationPacket CreateReplicationPacket(bool clearAckQueue = false)
	{
		ReplicationPacket packet = new(this.ReplicationQueue.Values.ToArray(), this.AckQueue.Values.ToArray());
		if (clearAckQueue)
			this.ClearAck();
		return packet;
	}

	/// <summary>
	/// Clears the acknowledgment queue. This method should be called by the ReplicationManager after it sends a packet
	/// containing acknowledgment data to the peer, so that we don't send the same acknowledgment data again in the next
	/// packet.
	/// </summary>
	public void ClearAck()
		=> this.AckQueue.Clear();
}
