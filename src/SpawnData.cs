using System;
using Godot;

namespace Raele.MultiplayerReplication;

public partial class SpawnData : GodotObject
{
	// -----------------------------------------------------------------------------------------------------------------
	// STATICS
	// -----------------------------------------------------------------------------------------------------------------

	public static Variant Serialize(SpawnData data)
		=> new Godot.Collections.Array
		{
			data.ReplicatorId.ToByteArray(),
			ResourceUid.TextToId(data.SceneUid),
			data.SpawnParentIndex,
			data.InitialValues
		};

	public static SpawnData Deserialize(Variant variant)
	{
		Godot.Collections.Array array = variant.AsGodotArray();
		return new SpawnData()
		{
			ReplicatorId = new Guid(array[0].AsByteArray()),
			SceneUid = ResourceUid.IdToText(array[1].AsInt64()),
			SpawnParentIndex = array[2].AsByte(),
			InitialValues = array[3].AsGodotArray<Variant>()
		};
	}

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	public required Guid ReplicatorId { get; init; }
	public required string SceneUid { get; init; }
	public required byte SpawnParentIndex { get; init; }
	public required Godot.Collections.Array<Variant> InitialValues { get; init; }

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

	public string SceneFilePath => ResourceUid.UidToPath(this.SceneUid);

	// -----------------------------------------------------------------------------------------------------------------
	// INTERNAL TYPES
	// -----------------------------------------------------------------------------------------------------------------

	// private enum Type {
	// 	Value1,
	// }

	// -----------------------------------------------------------------------------------------------------------------
	// METHODS
	// -----------------------------------------------------------------------------------------------------------------


}
