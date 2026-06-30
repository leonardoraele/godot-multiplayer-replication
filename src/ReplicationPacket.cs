using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

public record ReplicationPacket(ReplicationData[] ReplicationData, AckData[] AckData)
{
	public bool Empty => this.ReplicationData.Length == 0 && this.AckData.Length == 0;

	public static Variant Serialize(ReplicationPacket packet)
		=> new Godot.Collections.Array()
		{
			new Godot.Collections.Array(packet.ReplicationData.Select(MultiplayerReplication.ReplicationData.Serialize).ToArray()),
			new Godot.Collections.Array(packet.AckData.Select(MultiplayerReplication.AckData.Serialize).ToArray()),
		};

	public static ReplicationPacket Deserialize(Variant data)
	{
		ReplicationData[] replicationData = data.AsGodotArray()[0].AsGodotArray()
			.Select(MultiplayerReplication.ReplicationData.Deserialize)
			.ToArray();
		AckData[] ackData = data.AsGodotArray()[1].AsGodotArray()
			.Select(MultiplayerReplication.AckData.Deserialize)
			.ToArray();
		return new(replicationData, ackData);
	}

	public override string ToString()
		=> nameof(ReplicationPacket);
}
