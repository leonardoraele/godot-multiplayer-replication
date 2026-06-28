using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

public record ReplicationPacket(ReplicationData[] ReplicationData, AckData[] AckData)
{
	public Variant Serialize()
		=> new Godot.Collections.Array()
		{
			new Godot.Collections.Array(this.ReplicationData.Select(data => data.Serialize()).ToArray()),
			new Godot.Collections.Array(this.AckData.Select(data => data.Serialize()).ToArray()),
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
}
