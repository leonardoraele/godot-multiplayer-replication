using System;
using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

/// <summary>
/// Represents the replication data queued to update one specific object of a remote peer.
/// </summary>
public class ReplicationData
{
	public ReplicationData(Guid replicatorId) => this.ReplicatorId = replicatorId;
	public ReplicationData(Guid replicatorId, uint fieldMask, Variant[] values)
	{
		this.ReplicatorId = replicatorId;
		this.FieldMask = fieldMask;
		this.Values = values;
	}

	/// <summary>
	/// The unique cross-network identifier of the MultiplayerReplicator that should handle this replication data.
	/// </summary>
	public readonly Guid ReplicatorId;

	/// <summary>
	/// The bitmask representing which fields of the object the data corresponds to. Each bit corresponds to a field
	/// index, where a set bit indicates that the corresponding field has been modified and should be replicated to the
	/// remote peer.
	/// </summary>
	public uint FieldMask { get; private set; } = 0u;

	/// <summary>
	/// The array of values corresponding to the modified fields of the object. The values are ordered according to
	/// the field indices, and only the values for the fields indicated by the <see cref="FieldMask"/> are included in
	/// this array. The length of the array is equal to the number of set bits in the <see cref="FieldMask"/>.
	/// </summary>
	public Variant[] Values { get; private set; } = new Variant[32];

	/// <summary>
	/// Merges replication data into this instance. The provided field mask and values are combined with the existing
	/// data. New fields are added, and existing fields are updated with the new values.
	///
	/// The "inplace" in the method name indicates that the operation modifies the current instance of this class.
	/// </summary>
	public void MergeInplace(uint fieldMask, Variant[] values)
	{
		// Update the field mask to include the new fields
		this.FieldMask |= fieldMask;

		// Merge the new values into the existing values array
		for (int i = 0; i < 32; i++)
		{
			if ((fieldMask & (1u << i)) != 0)
				this.Values[i] = values[i];
		}
	}

	/// <summary>
	/// Acknowledges the replication data for the specified fields. This method removes the acknowledged fields from
	/// the field mask and clears their corresponding values. If all fields have been acknowledged, the method returns
	/// true, indicating that the replication data is fully acknowledged and can be removed from the queue.
	///
	/// The "inplace" in the method name indicates that the operation modifies the current instance of this class.
	/// </summary>
	/// <param name="fieldMask">The bitmask representing the fields to acknowledge.</param>
	/// <returns>True if all fields have been acknowledged; otherwise, false.</returns>
	public bool AckInplace(uint fieldMask)
	{
		// Remove the acknowledged fields from the field mask
		this.FieldMask &= ~fieldMask;

		// Clear the values for the acknowledged fields
		for (int i = 0; i < 32; i++)
		{
			if ((fieldMask & (1u << i)) != 0)
				this.Values[i] = new Variant();
		}

		return this.FieldMask == 0;
	}

	public Variant Serialize()
		=> new Godot.Collections.Array()
		{
			this.ReplicatorId.ToByteArray(),
			this.FieldMask,
			new Godot.Collections.Array(
				Enumerable.Range(0, 32)
					.Where(i => (this.FieldMask & (1u << i)) != 0)
					.Select(i => this.Values[i])
					.ToArray()
			),
		};

	public static ReplicationData Deserialize(Variant data)
	{
		Guid replicatorId = new(data.AsGodotArray()[0].AsByteArray());
		uint fieldMask = data.AsGodotArray()[1].AsUInt32();
		Variant[] values = data.AsGodotArray()[2].AsGodotArray().ToArray();
		return new(replicatorId, fieldMask, values);
	}
}
