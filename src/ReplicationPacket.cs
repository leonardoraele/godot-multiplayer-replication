using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

public record ReplicationPacket(ReplicationData[] ReplicationData, AckData[] AckData)
{
	public bool Empty => this.ReplicationData.Length == 0 && this.AckData.Length == 0;

	public static Variant Serialize(ReplicationPacket packet)
		=> new Godot.Collections.Array()
		{
			new Godot.Collections.Array(packet.ReplicationData.Select(data => data.Serialize()).ToArray()),
			new Godot.Collections.Array(packet.AckData.Select(data => data.Serialize()).ToArray()),
		};

	public static ReplicationPacket Deserialize(Variant data)
	{
		ReplicationData[] replicationData = data.AsGodotArray()[0].AsGodotArray()
			.Select(variant => MultiplayerReplication.ReplicationData.Deserialize(variant))
			.ToArray();
		AckData[] ackData = data.AsGodotArray()[1].AsGodotArray()
			.Select(variant => MultiplayerReplication.AckData.Deserialize(variant))
			.ToArray();
		return new(replicationData, ackData);
	}

	public override string ToString()
		=> $"{nameof(ReplicationPacket)} {new {
			UpdatedNodes = this.ReplicationData.Length,
			Acks = this.AckData.Length,
		}}";
}
