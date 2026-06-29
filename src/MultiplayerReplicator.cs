using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

/// <summary>
/// The MultiplayerReplicator tracks the replicated fields of its parent node. When the fields change on the multiplayer
/// authority peer, they are automatically replicated to the interested peers.
///
/// The MultiplayerReplicator also makes sure its parent node is correctly spawned and despawned on all peers when it
/// enters or leaves the scene tree.
/// </summary>
[Tool][GlobalClass]
public partial class MultiplayerReplicator : Node
{
	// -----------------------------------------------------------------------------------------------------------------
	// STATICS
	// -----------------------------------------------------------------------------------------------------------------

	public const string GROUP_NAME = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(MultiplayerReplicator)}";

	// -----------------------------------------------------------------------------------------------------------------
	// EXPORTS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// A list of field names to replicate, separated by comma. This MultiplayerReplicator will replicate these fields
	/// in addition to the fields marked with the [Replicated] attribute.
	///
	/// Each entry of this list can be a direct property of the replicated node (i.e. the parent of this node) or a
	/// property path to a nested field, using NodePath notation. For example, ":position:x" will replicate only the X
	/// component of the replicated node's `position` property.
	/// </summary>
	[Export] public Godot.Collections.Array<string> ReplicatedFields = [];

	[Export] public bool Interpolate = true;

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The unique identifier for this MultiplayerReplicator. This ID is used to identify the replicated object across
	/// the network. It is generated when the MultiplayerReplicator is created and remains constant throughout its
	/// lifetime.
	/// </summary>
	/// <remarks>
	/// This field is exported so that it can be stored in the scene file and remain constant across game sessions; but
	/// it is hidden in the editor and automatically generated when the MultiplayerReplicator is created. It should not
	/// be modified by the user.
	/// </remarks>
	[Export] private string SerializedReplicatorId
	{
		get => this.ReplicatorId.ToString();
		set => this.ReplicatorId = Guid.TryParse(value, out Guid guid) ? guid : Guid.Empty;
	}

	/// <summary>
	/// Stores the values of the replicated fields in the same order as the <see cref="ReplicatedFields"/> array.
	/// Updated every frame.
	/// </summary>
	private Variant[] ReplicatedFieldsCache
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length)
				field = new Variant[this.ReplicatedFields.Count];
			return field;
		}
	}

	// /// <summary>
	// /// Bitmask that marks the fields that have changed since the last time they were sent to the peers.
	// /// </summary>
	// private uint DirtyFieldsMask = 0u;

	/// <summary>
	/// Holds a reference to the parent node so that we don't need to call godot api every frame.
	/// </summary>
	private Node ParentCache => Engine.IsEditorHint()
		? this.GetParent()
		: (field ??= this.GetParent());

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The unique identifier for this MultiplayerReplicator across hosts.
	/// </summary>
	public Guid ReplicatorId = Guid.NewGuid();

	// -----------------------------------------------------------------------------------------------------------------
	// SIGNALS
	// -----------------------------------------------------------------------------------------------------------------

	// [Signal] public delegate void EventHandler()

	// -----------------------------------------------------------------------------------------------------------------
	// INTERNAL TYPES
	// -----------------------------------------------------------------------------------------------------------------


	// -----------------------------------------------------------------------------------------------------------------
	// EVENTS
	// -----------------------------------------------------------------------------------------------------------------

	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		base._ValidateProperty(property);
		switch (property["name"].AsString()) {
			case nameof(this.SerializedReplicatorId):
				property["usage"] = (long) PropertyUsageFlags.Storage;
				break;
			case nameof(this.ReplicatedFields):
				string propNames = this.ParentCache?.GetPropertyList()
					.Where(prop => (prop["usage"].AsInt64() & (long) PropertyUsageFlags.Storage) != 0)
					.Select(prop => prop["name"].AsString())
					.ToArray()
					.Join(",")
					?? "";
				property["type"] = (long) Variant.Type.Array;
				property["hint"] = (long) PropertyHint.ArrayType;
				property["hint_string"] = $"{Variant.Type.String:D}/{PropertyHint.EnumSuggestion:D}:{propNames}";
				break;
		}
	}

	public override void _EnterTree()
	{
		base._EnterTree();
		this.AddToGroup(GROUP_NAME);
		if (Engine.IsEditorHint())
			return;
		ReplicationManager.Instance.RegisterReplicator(this);
	}

    public override void _ExitTree()
	{
		base._ExitTree();
		if (Engine.IsEditorHint())
			return;
		ReplicationManager.Instance.UnregisterReplicator(this);
	}

	// public override void _Ready()
	// {
	// 	base._Ready();
	// }

	// public override void _Process(double delta)
	// {
	// 	base._Process(delta);
	// }

	// public override void _PhysicsProcess(double delta)
	// {
	// 	base._PhysicsProcess(delta);
	// }

	// public override string[] _GetConfigurationWarnings()
	// 	=> base._PhysicsProcess(delta);

	// -----------------------------------------------------------------------------------------------------------------
	// METHODS
	// -----------------------------------------------------------------------------------------------------------------

	private IEnumerable<string> GetFieldNames(uint bitmask) => this.ReplicatedFields
		.Where((_, index) => (bitmask & (1u << index)) != 0);

	/// <summary>
	/// Gets the next replication data to be sent to the peers, if available. The replication data contains the values
	/// of the replicated fields that changed since this method was last called, in a packed format to be sent to the
	/// peers for state replication. The first time this method is called, it should contain replication data for all of
	/// the replicated fields.
	///
	/// This method returns false and the out parameter set to null if the multiplayer authority peer has no dirty
	/// fields or if this peer is not the multiplayer authority for this node, signifying there is no data to be
	/// transmitted by this host to other peers. The multiplayer authority peer is the only peer that should send
	/// replication data to the others.
	/// </summary>
	/// <param name="data">The replication data to be sent to the peers.</param>
	/// <returns>True if there is replication data to be sent, false otherwise.</returns>
	public bool TryGetNextReplicationData([NotNullWhen(true)] out ReplicationData? data)
	{
		data = null;
		if (!this.IsMultiplayerAuthority())
			return false;
		uint dirtyFields = this.GetNextDirtyFields();
		if (dirtyFields == 0u)
			return false;
		data = new(this.ReplicatorId, dirtyFields, [..this.ReplicatedFieldsCache]);
		// GD.PrintS(nameof(MultiplayerReplicator), this.Multiplayer.GetUniqueId(), $"Generated replication data for \"{this.ParentCache.Name}\" ({string.Join(", ", this.GetFieldNames(data.FieldMask))})");
		return data != null;
	}

	/// <summary>
	/// Updates the dirtiness of all fields; i.e. marks them as dirty if their values have changed since the last time
	/// this method was called.
	/// </summary>
	private uint GetNextDirtyFields()
	{
		// No need to update dirtiness if we are not the multiplayer authority, because only the multiplayer authority
		// should send replication data to the other peers. The other peers will receive the replication data from the
		// multiplayer authority and update their local values accordingly.
		if (!this.IsMultiplayerAuthority())
			return 0u;
		return Enumerable.Range(0, this.ReplicatedFields.Count)
			.Where(this.GetNextFieldDirtiness)
			.Select(index => 1u << index)
			.Aggregate(0u, (acc, value) => acc | value);
	}

	/// <summary>
	/// Updates the dirtiness of a field; i.e. marks it as dirty if its value has changed since the last time this
	/// method was called. Returns true if the field was marked as dirty, false otherwise.
	/// </summary>
	/// <returns>True if the field is dirty.</returns>
	private bool GetNextFieldDirtiness(int index)
	{
		string fieldName = this.ReplicatedFields[index];
		Variant currentValue = this.ParentCache.GetIndexed(fieldName);
		// GD.PrintS(nameof(MultiplayerReplicator), nameof(GetNextFieldDirtiness), this.Multiplayer.GetUniqueId(), new { fieldName, currentValue, cachedValue = this.ReplicatedFieldsCache[index], equals = currentValue.Equals(this.ReplicatedFieldsCache[index]) });
		if (!currentValue.Equals(this.ReplicatedFieldsCache[index]))
		{
			this.ReplicatedFieldsCache[index] = currentValue;
			return true;
		}
		return false;
	}

	/// <summary>
	/// Sets the values of the fields specified by the bitmask. This method is called by the
	/// <see cref="ReplicationManager"/> when it receives replication data from the multiplayer authority peer.
	/// </summary>
	public void AcceptReplicationData(ReplicationData data)
	{
		GD.PrintS(nameof(MultiplayerReplicator), this.Multiplayer.GetUniqueId(), $"Received replication data for \"{this.ParentCache.Name}\" ({string.Join(", ", this.GetFieldNames(data.FieldMask))})");
		string[] fieldNames = this.GetFieldNames(data.FieldMask).ToArray();
		Debug.Assert(fieldNames.Length == data.Values.Length, "Field names and values arrays must have the same length.");
		for (int i = 0; i < data.Values.Length; i++)
			this.AcceptNewFieldValue(this.ParentCache, fieldNames[i], data.Values[i]);
	}

	private void AcceptNewFieldValue(Node node, string fieldName, Variant newValue)
	{
		if (!this.Interpolate)
		{
			node.SetIndexed(fieldName, newValue);
			return;
		}
		Tween tween = this.CreateTween();
		double duration = ReplicationManager.Instance.GetReplicationInterpolationTime(this).TotalSeconds;
		tween.TweenProperty(node, fieldName, newValue, duration);
	}

	// private void OnPeerChangedInterest(ConnectedPeer peer)
	// {
	// 	if (
	// 		!peer.IsLocalPeer
	// 		&& this.IsMultiplayerAuthority()
	// 		&& this.ParentCache.GetGroups().All(group => peer.GroupsOfInterest.Contains(group))
	// 	) {
	// 		this.RpcId(peer.Id, MethodName.RpcSetValues, uint.MaxValue, this.GetLocalValues(uint.MaxValue));
	// 	}
	// 	// if (
	// 	// 	!peer.IsLocalPeer
	// 	// 	&& this.IsMultiplayerAuthority()
	// 	// 	&& peer.CurrentScene.Value == NetworkManager.Instance.LocalPeer.CurrentScene.Value
	// 	// 	// Spawned nodes can't be synchronized at scene-change time because they are not in the scene tree yet. They
	// 	// 	// will be synchronized when they are spawned. (user should call Update() manually at _NetworkSpawned())
	// 	// 	&& !NetworkManager.Spawner.CheckIsNetworkSpawned(this.ParentCache)
	// 	// ) {
	// 	// 	this.RpcId(peer.Id, MethodName.RpcSetValues, uint.MaxValue, this.GetLocalValues(uint.MaxValue));
	// 	// }
	// }
}
