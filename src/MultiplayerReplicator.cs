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

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The unique identifier for this MultiplayerReplicator. This ID is used to identify the replicated object across
	/// the network. It is generated when the MultiplayerReplicator is created and remains constant throughout its
	/// lifetime.
	/// </summary>
	public Guid ReplicatorId { get; init; } = Guid.NewGuid();

	/// <summary>
	/// Stores the values of the replicated fields in the same order as the <see cref="ReplicatedFields"/> array.
	/// Updated every frame.
	/// </summary>
	private Variant[] ReplicatedFieldsCache
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length) {
				field = this.ReplicatedFields
					.Select(fieldName => this.ParentCache.GetIndexed(fieldName))
					.ToArray();
				this.MarkAllFieldsDirty();
			}
			return field;
		}
	}

	/// <summary>
	/// Bitmask that marks the fields that have changed since the last time they were sent to the peers.
	/// </summary>
	private uint DirtyFieldsMask = 0u;

	/// <summary>
	/// Holds a reference to the parent node so that we don't need to call godot api every frame.
	/// </summary>
	private Node ParentCache => field ??= this.GetParent();

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

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
			case nameof(ReplicatedFields):
				string propNames = this.ParentCache.GetPropertyList()
					.Where(prop => (prop["usage"].AsInt64() & (long) PropertyUsageFlags.Storage) != 0)
					.Select(prop => prop["name"].AsString())
					.ToArray()
					.Join(",");
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
		ReplicationManager.Instance.RegisterReplicator(this);
	}

    public override void _ExitTree()
	{
		base._ExitTree();
		ReplicationManager.Instance.UnregisterReplicator(this);
	}

	public override void _Ready()
	{
		base._Ready();
		this.MarkAllFieldsDirty();
	}

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
	// DIRTY FIELD HELPER METHODS
	// -----------------------------------------------------------------------------------------------------------------

	private bool IsFieldDirty(int index) => (this.DirtyFieldsMask & (1u << index)) != 0;
	private bool HasDirtyFields() => this.DirtyFieldsMask != 0;

	private void MarkFieldDirty(int index) => this.MarkFieldsDirty(1u << index);
	private void MarkAllFieldsDirty()
	{
		for (int i = 0; i < ReplicatedFields.Count; i++)
			this.MarkFieldDirty(i);
	}
	private void MarkFieldsDirty(uint bitmask) => this.DirtyFieldsMask |= bitmask;
	private void MarkFieldsClean(uint bitmask) => this.DirtyFieldsMask &= ~bitmask;

	private IEnumerable<string> GetFieldNames(uint bitmask) => this.ReplicatedFields
		.Where((_, index) => (bitmask & (1u << index)) != 0);
	private IEnumerable<string> GetDirtyFieldNames() => this.GetFieldNames(this.DirtyFieldsMask);
	private IEnumerable<Variant> GetDirtyFieldValues() => this.GetDirtyFieldNames()
		.Select(field => this.ParentCache.GetIndexed(field));


	// -----------------------------------------------------------------------------------------------------------------
	// METHODS
	// -----------------------------------------------------------------------------------------------------------------

	public bool TryGetReplicationData([NotNullWhen(true)] out ReplicationData? data)
	{
		if (this.IsMultiplayerAuthority())
		{
			this.UpdateDirtiness();
			data = new(this.ReplicatorId, this.DirtyFieldsMask, this.GetDirtyFieldValues().ToArray());
		}
		else
			data = null;
		return data != null;
	}

	/// <summary>
	/// Updates the dirtiness of all fields; i.e. marks them as dirty if their values have changed since the last time
	/// this method was called.
	/// </summary>
	private void UpdateDirtiness()
	{
		// No need to update dirtiness if we are not the multiplayer authority, because only the multiplayer authority
		// should send replication data to the other peers. The other peers will receive the replication data from the
		// multiplayer authority and update their local values accordingly.
		if (!this.IsMultiplayerAuthority())
			return;
		for (int i = 0; i < this.ReplicatedFields.Count; i++)
			this.UpdateFieldDirtiness(i);
	}

	/// <summary>
	/// Updates the dirtiness of a field; i.e. marks it as dirty if its value has changed since the last time this
	/// method was called. Returns true if the field was marked as dirty, false otherwise.
	/// </summary>
	/// <returns>True if the field is dirty.</returns>
	private void UpdateFieldDirtiness(int index)
	{
		if (this.IsFieldDirty(index))
			return;
		string field = this.ReplicatedFields[index];
		Variant currentValue = this.ParentCache.GetIndexed(field);
		bool fieldChanged = !currentValue.Equals(this.ReplicatedFieldsCache[index]);
		this.ReplicatedFieldsCache[index] = currentValue;
		if (fieldChanged)
			this.MarkFieldDirty(index);
	}

	/// <summary>
	/// Sets the values of the fields specified by the bitmask. This method is called by the
	/// <see cref="ReplicationManager"/> when it receives replication data from the multiplayer authority peer.
	/// </summary>
	public void AcceptReplicationData(ReplicationData data)
	{
		string[] fieldNames = this.GetFieldNames(data.FieldMask).ToArray();
		Debug.Assert(fieldNames.Length == data.Values.Length, "Field names and values arrays must have the same length.");
		for (int i = 0; i < data.Values.Length; i++) {
			this.ParentCache.SetIndexed(fieldNames[i], data.Values[i]);
		}
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
