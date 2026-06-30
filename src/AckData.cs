using System;
using System.Diagnostics;
using System.Numerics;
using Godot;

namespace Raele.MultiplayerReplication;

public record AckData(Guid ReplicatorId, uint FieldMask)
{
	public AckData Union(AckData ackData)
		=> new(this.ReplicatorId, this.FieldMask | ackData.FieldMask);

	public AckData Difference(uint fieldMask)
		=> new(this.ReplicatorId, this.FieldMask & ~fieldMask);

	public bool Empty => this.FieldMask == 0u;
	public int FieldCount => BitOperations.PopCount(this.FieldMask);

	public static Variant Serialize(AckData data)
		=> new Godot.Collections.Array()
		{
			data.ReplicatorId.ToByteArray(),
			data.FieldMask,
		};

	public static AckData Deserialize(Variant data)
	{
		Godot.Collections.Array array = data.AsGodotArray();
		Debug.Assert(array.Count == 2, "AckData serialization must contain exactly 2 elements.");
		Guid replicatorId = new(array[0].AsByteArray());
		uint fieldMask = array[1].AsUInt32();
		return new(replicatorId, fieldMask);
	}

	public override string ToString()
		=> $"{nameof(AckData)} {new { this.FieldCount, this.ReplicatorId }}";
}
