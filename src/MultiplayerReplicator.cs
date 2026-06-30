using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Godot;
using Raele.GodotUtils.Extensions;

namespace Raele.MultiplayerReplication;

/// <summary>
/// This node allows you to define a list of node properties to be replicated to other peers. You can define how data
/// should be replicated (unreliably every frame, reliably on change, or manually) and whether received values are
/// directly assigned to the properties or smoothly interpolated over time.
///
/// You can also define spawn parents. These are special parent nodes are intended to host dynamically instantiated
/// child nodes. This node will observe these parents and, whenever a child node is added to them, it will automatically
/// be replicated to the other peers. Likewise, when a child node is removed from one of these spawn parents, it will be
/// automatically despawned on the other peers. This is useful for synchronizing dynamically spawned objects, such as
/// bullets, enemies, or other objects that are not part of the initial scene tree.
///
/// Note that, because this node only replicate explicitly defined properties, it won't replicate properties of
/// dynamically spawned nodes -- those nodes must have their own <see cref="MultiplayerReplicator"/> nodes to replicate
/// their properties.
///
/// For example, if you have an Enemy scene that has a <see cref="MultiplayerReplicator"/> node, it can replicate
/// properties such as position and health to the other peers. It can also replicate spawning Projectile scenes that it
/// instantiates. However, it cannot replicate the properties of the Projectile scene, because it doesn't know which
/// properties to replicate. The Projectile scene must have its own <see cref="MultiplayerReplicator"/> node to
/// replicate its properties, such as position and velocity, to the other peers.
///
/// // TODO:
/// - Allow to programmatically configure which peers each property should be replicated to.
/// - Allow client-side prediction of replicated properties, with automatic reconciliation when the server sends the
/// 	authoritative value.
/// </summary>
[Tool][GlobalClass]
public partial class MultiplayerReplicator : Node
{
	// -----------------------------------------------------------------------------------------------------------------
	// STATICS
	// -----------------------------------------------------------------------------------------------------------------

	public const string GROUP_REPLICATORS = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(MultiplayerReplicator)}";
	public const string GROUP_SPAWNED = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(MultiplayerReplicator)}.Spawned";

	// -----------------------------------------------------------------------------------------------------------------
	// EXPORTS
	// -----------------------------------------------------------------------------------------------------------------

	[ExportGroup("Property Replication")]
	/// <summary>
	/// The list of field to replicate. Each entry is a node path with property path. The node path is relative to the
	/// scene root. (i.e. the owner of this node)
	/// </summary>
	[Export] public Godot.Collections.Array<string> ReplicatedFields = [];

	[ExportToolButton("Add Property...")] public Callable AddReplicationFieldButton
		=> Callable.From(this.OnAddReplicationFieldButtonPressed);

	[ExportSubgroup("Additional Options")]
	/// <summary>
	/// If true, the replicated fields will be interpolated when they are updated on the peers. If false, the fields
	/// will be updated immediately to the newly received values. Interpolation is useful for smooth movement of
	/// objects, but it can introduce a small delay.
	/// </summary>
	[Export] public bool InterpolateValues = true;

	/// <summary>
	/// Defines how the replication data is sent to the peers. The default is to send the data reliably only when it
	/// changes. This is the recommended method for most properties, as it reduces bandwidth usage and ensures that the
	/// data is always consistent across peers.
	///
	/// If you need different replication strategies for different properties, add separate
	/// <see cref="MultiplayerReplicator"/> nodes, each with its own replication strategy.
	/// </summary>
	[Export] public ReplicationStrategyEnum ReplicationStrategy = ReplicationStrategyEnum.ReliablyOnChange;

	[ExportGroup("Spawning & Despawning Replication")]
	/// <summary>
	/// Whenever a child node is added to one of the nodes in this list, it will be automatically replicated to the
	/// other peers -- meaning they will also instantiate the same scene and add it to this parent node. This is useful
	/// for synchronizing dynamically spawned objects, such as bullets, enemies, or other objects that are not part of
	/// the initial scene tree.
	///
	/// Likewise, whenever a child node is removed from one of these spawn parents, it will be automatically despawned
	/// on the other peers.
	/// </summary>
	[Export] public Godot.Collections.Array<Node> SpawnParents = [];

	[ExportSubgroup("Additional Options")]
	/// <summary>
	/// If this list is not empty, only these explicitly listed scenes will be replicated to the other peers.
	/// </summary>
	[Export] public string[] SceneSpawnWhitelist = [];

	/// <summary>
	/// If this is enabled, this node will not despawn the replicated nodes when they are removed from their spawn
	/// parents. This is useful to reduce bandwidth usage in cases where the clients can determine when to destroy the
	/// spawned nodes by themselves.
	/// </summary>
	[Export] public bool DisableAutomaticDespawn = false;

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Stores the values of the replicated fields in the same order as the fields are registered in the
	/// <see cref="ReplicatedFields"/> array. Updated every frame.
	/// </summary>
	private Variant[] ReplicatedFieldOldValues
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length)
				field = new Variant[this.ReplicatedFields.Count];
			return field;
		}
	}

	private Variant[] ReplicatedFieldNewValues
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length)
				field = new Variant[this.ReplicatedFields.Count];
			return field;
		}
	}

	private Func<Variant>[] ReplicatedFieldGetters
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length)
				field = this.ReplicatedFields.Select<string, Func<Variant>>(fieldPath =>
						this.GetFieldData(fieldPath, out Node? subject, out string? fieldName, out _)
							? () => subject.GetIndexed(fieldName)
							: () => new Variant()
					)
					.ToArray();
			return field;
		}
	}

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Unique identifier for this MultiplayerReplicator across its local owned scene.
	///
	/// // TODO This could be a single-byte digit if we use tool scripts to automatically assign unique ids whenever a
	/// node is added/removed from the scene.
	/// </summary>
	private byte SceneId => this.GetTree()
		.GetNodesInGroup(GROUP_REPLICATORS)
		.OfType<MultiplayerReplicator>()
		.Where(replicator => replicator.Owner == this.Owner)
		.ToList()
		.SortInplace((r1, r2) => string.Compare(r1.Name, r2.Name, StringComparison.Ordinal))
		.Select((replicator, index) => (replicator, (byte) index))
		.First(tuple => tuple.replicator == this)
		.Item2;

	/// <summary>
	/// Unique identifier for this MultiplayerReplicator across the project. This has the same value
	/// </summary>
	private byte[] StaticId
	{
		get
		{
			long uid = ResourceUid.TextToId($"{ResourceUid.PathToUid(this.Owner.SceneFilePath)}/{this.Name}");
			byte[] bytes = BitConverter.GetBytes(uid);
			byte[] result = new byte[9];
			Array.Copy(bytes, 0, result, 0, Math.Min(bytes.Length, 8));
			result[8] = SceneId;
			return result;
		}
	}

	/// <summary>
	/// The unique identifier for this MultiplayerReplicator across the network.
	/// </summary>
	public Guid NetworkId;

	// -----------------------------------------------------------------------------------------------------------------
	// SIGNALS
	// -----------------------------------------------------------------------------------------------------------------

	[Signal] public delegate void ReplicationDataEventHandler(ReplicationData data);

	// -----------------------------------------------------------------------------------------------------------------
	// INTERNAL TYPES
	// -----------------------------------------------------------------------------------------------------------------

	public enum ReplicationStrategyEnum
	{
		/// <summary>
		/// Replication data is sent to the peers every frame, regardless of whether the values have changed or not.
		///
		/// This is the recommended method for properties that change very frequently -- more than once for every two
		/// frames. For example, the position, rotation, and velocity of a car in a racing game. In these cases, this
		/// method is more efficient because it avoids the overhead of reliable communication and allows for more
		/// efficient use of bandwidth.
		/// </summary>
		UnreliablyEveryFrame,

		/// <summary>
		/// Replication data is reliably sent to the peers only when the values have changed since the last time they
		/// were sent.
		///
		/// This method is recommended for properties that change only occasionally. For example, the health of a player
		/// in a first-person shooter game. In these cases, this method is more efficient because it avoids sending
		/// unnecessary data when the values have not changed.
		/// </summary>
		ReliablyOnChange,

		/// <summary>
		/// Replication data is sent to the peers only when the the <see cref="ForceReplicate"/> method is called by the
		/// user.
		///
		/// This is useful for properties that are usually not relevant to the other peers, but that can be replicated
		/// on demand. For example, a player can request to see the inventory of another player, and the inventory can
		/// be replicated only when the request is made.
		/// </summary>
		ReliablyOnDemand,
	}

	// -----------------------------------------------------------------------------------------------------------------
	// EVENTS
	// -----------------------------------------------------------------------------------------------------------------

	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		base._ValidateProperty(property);
		switch (property["name"].AsString()) {
			case nameof(this.SceneSpawnWhitelist):
				property["type"] = (long) Variant.Type.Array;
				property["hint"] = (long) PropertyHint.ArrayType;
				property["hint_string"] = $"{Variant.Type.String:D}/{PropertyHint.File:D}:*.tscn,*.scn";
				break;
		}
	}

	public override string[] _GetConfigurationWarnings()
	{
		List<string> warnings = [.. base._GetConfigurationWarnings() ?? []];
		if (!this.UniqueNameInOwner)
			warnings.Add($"The {nameof(MultiplayerReplicator)} node must have a unique name. This is necessary to facilitate identifying it across the network. Please right-click this node and enable \"Access as Unique Name\" for this node.");
		foreach (string field in this.ReplicatedFields)
			if (!this.GetFieldData(field, out Node? subject, out string? fieldName, out Variant value))
				warnings.Add($"The field \"{field}\" is invalid. See console log for details.");
		return warnings.ToArray();
	}

	public override void _EnterTree()
	{
		base._EnterTree();
		this.AddToGroup(GROUP_REPLICATORS, persistent: true);
		if (this.Owner.IsInGroup(GROUP_SPAWNED))
		{
			// TODO Handle when it is spawned by another peer.
		}
		if (Engine.IsEditorHint())
			return;
		ReplicationManager.Instance.RegisterReplicator(this);
		ReplicationManager.Instance.NetworkProcess += this.ProcessNetwork;
	}

    public override void _ExitTree()
	{
		base._ExitTree();
		if (Engine.IsEditorHint())
			return;
		ReplicationManager.Instance.UnregisterReplicator(this);
		ReplicationManager.Instance.NetworkProcess -= this.ProcessNetwork;
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

	private void ProcessNetwork()
	{
		if (this.ReplicationStrategy != ReplicationStrategyEnum.ReliablyOnDemand)
			this.ForceReplicate();
	}

	// -----------------------------------------------------------------------------------------------------------------
	// METHODS
	// -----------------------------------------------------------------------------------------------------------------

	private void ForceReplicate()
	{
		this.UpdateReplicatedFieldsCache();
		ReplicationData? data = this.BuildReplicationData();
		if (data != null)
			this.EmitSignal(SignalName.ReplicationData, data);
	}

	[Obsolete]
	private IEnumerable<string> GetFieldNames(uint bitmask) => this.ReplicatedFields
		.Where((_, index) => (bitmask & (1u << index)) != 0);

	/// <summary>
	/// Updates the cached values of the replicated fields. This is necessary to detect when a field has changed its
	/// value.
	/// </summary>
	private void UpdateReplicatedFieldsCache()
	{
		for (int i = 0; i < this.ReplicatedFields.Count; i++)
		{
			this.ReplicatedFieldOldValues[i] = this.ReplicatedFieldNewValues[i];
			this.ReplicatedFieldNewValues[i] = this.ReplicatedFieldGetters[i]();
		}
	}

	/// <summary>
	/// Gets the next replication data to be sent to the peers, if available. The replication data contains the values
	/// of the replicated fields that should be sent to the other peers according to the selected
	/// <see cref="ReplicationStrategy"/>.
	///
	/// <list type="bullet">
	/// <item>For UnreliablyEveryFrame, the replications data always contains the most recent value for all replicated fields.</item>
	/// <item>For ReliablyOnChange and ReliablyOnDemand, this method returns the most recent value for the fields that changed since the last time this method was called.</item>
	/// </list>
	///
	/// This method returns null if there is no replication data to be sent, either because there are no dirty fields or
	/// because this peer is not the multiplayer authority for this node. (the multiplayer authority peer is the only
	/// peer that should send replication data to the others)
	/// </summary>
	private ReplicationData? BuildReplicationData()
		=> !this.IsMultiplayerAuthority()
			? null
			: this.ReplicationStrategy switch {
				ReplicationStrategyEnum.UnreliablyEveryFrame => this.BuildReplicationDataWithAllFields(),
				ReplicationStrategyEnum.ReliablyOnChange
					or ReplicationStrategyEnum.ReliablyOnDemand => this.BuildReplicationDataWithChangedFields(),
				_ => null,
			};

	private ReplicationData? BuildReplicationDataWithAllFields()
		=> new(this.NetworkId, uint.MaxValue, this.ReplicatedFieldNewValues);

	private ReplicationData? BuildReplicationDataWithChangedFields()
	{
		List<int> dirtyIndexes = Enumerable.Range(0, this.ReplicatedFields.Count)
			.Where(this.TestFieldIsDirty)
			.ToList();
		if (dirtyIndexes.Count == 0)
			return null;
		uint dirtyFields = dirtyIndexes.Aggregate(0u, (acc, index) => acc.SetBit(index));
		Variant[] newValues = dirtyIndexes.Select(index => this.ReplicatedFieldNewValues[index]).ToArray();
		return new(this.NetworkId, dirtyFields, newValues);
	}

	/// <summary>
	/// Updates the dirtiness of all fields; i.e. marks them as dirty if their values have changed since the last time
	/// this method was called.
	/// </summary>
	private uint GetDirtyFields()
	{
		// No need to update dirtiness if we are not the multiplayer authority, because only the multiplayer authority
		// should send replication data to the other peers. The other peers will receive the replication data from the
		// multiplayer authority and update their local values accordingly.
		if (!this.IsMultiplayerAuthority())
			return 0u;
		return Enumerable.Range(0, this.ReplicatedFields.Count)
			.Where(this.TestFieldIsDirty)
			.Select(index => 1u << index)
			.Aggregate(0u, (acc, value) => acc | value);
	}

	/// <summary>
	/// Updates the dirtiness of a field; i.e. marks it as dirty if its value has changed since the last time this
	/// method was called. Returns true if the field was marked as dirty, false otherwise.
	/// </summary>
	/// <returns>True if the field is dirty.</returns>
	private bool TestFieldIsDirty(int index)
		=> !this.ReplicatedFieldNewValues[index].Equals(this.ReplicatedFieldOldValues[index]);

	/// <summary>
	/// Sets the values of the fields specified by the bitmask. This method is called by the
	/// <see cref="ReplicationManager"/> when it receives replication data from the multiplayer authority peer.
	/// </summary>
	public void AcceptReplicationData(ReplicationData data)
	{
		if (this.Root == null)
			return;
		GD.PrintS(nameof(MultiplayerReplicator), this.Multiplayer.GetUniqueId(), $"Received replication data for \"{this.Root.Name}\" ({string.Join(", ", this.GetFieldNames(data.FieldMask))})");
		string[] fieldNames = this.GetFieldNames(data.FieldMask).ToArray();
		Debug.Assert(fieldNames.Length == data.Values.Length, "Field names and values arrays must have the same length.");
		for (int i = 0; i < data.Values.Length; i++)
			this.AcceptNewFieldValue(fieldNames[i], data.Values[i]);
	}

	private void AcceptNewFieldValue(string fieldName, Variant newValue)
	{
		if (this.Root == null)
			return;
		if (!this.InterpolateValues)
		{
			this.Root.SetIndexed(fieldName, newValue);
			return;
		}
		Tween tween = this.CreateTween();
		double duration = ReplicationManager.Instance.GetReplicationInterpolationTime(this).TotalSeconds;
		tween.TweenProperty(this.Root, fieldName, newValue, duration);
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

	private void OnAddReplicationFieldButtonPressed()
	{
		if (this.Root == null)
		{
			GD.PrintErr(nameof(MultiplayerReplicator), $"Cannot add replication field because the mandatory field '{nameof(this.Root)}' is not set.");
			return;
		}

		EditorInterface.Singleton.PopupNodeSelector(Callable.From((NodePath path) =>
		{
			Node subject = this.Owner.GetNode(path);
			EditorInterface.Singleton.PopupPropertySelector(subject, Callable.From((string property) =>
			{
				this.ReplicatedFields.Add($"{this.Root.GetPathTo(subject)}:{property}");
				this.NotifyPropertyListChanged();
			}));
		}));
	}

	private void MigrateReplicatedFieldPaths(Node? oldTarget, Node? newTarget)
	{
		if (oldTarget == null || newTarget == null)
			return;
		this.ReplicatedFields = this.ReplicatedFields.Select(fieldPath =>
			{
				if (!this.GetFieldData(fieldPath, out Node? subject, out string? fieldName, out _))
					return fieldPath;
				return $"{newTarget.GetPathTo(subject)}::{fieldName}";
			})
			.ToGodotArrayT();
		this.NotifyPropertyListChanged();
	}

	private bool GetFieldDataAt(
		int index,
		[NotNullWhen(true)] out Node? subject,
		[NotNullWhen(true)] out string? fieldName,
		out Variant value
	)
		=> this.GetFieldData(this.ReplicatedFields[index], out subject, out fieldName, out value);
	private bool GetFieldData(
		string fieldPath,
		[NotNullWhen(true)] out Node? subject,
		[NotNullWhen(true)] out string? fieldName,
		out Variant value
	)
	{
		subject = null;
		value = new Variant();
		(string? nodePath, fieldName) = fieldPath.Split("::");
		if (string.IsNullOrWhiteSpace(nodePath) || string.IsNullOrWhiteSpace(fieldName))
		{
			GD.PushWarning($"{nameof(MultiplayerReplicator)}: Failed to get value for field \"{fieldPath}\". Cause: it should be in the format \"NodePath::PropertyName\".");
			return false;
		}
		subject = this.Owner.GetNode(nodePath);
		if (subject == null)
		{
			GD.PushWarning($"{nameof(MultiplayerReplicator)}: Failed to get value for field \"{fieldPath}\". Cause: the node at path \"{nodePath}\" does not exist.");
			return false;
		}
		value = subject.GetIndexed(fieldName);
		return true;
	}
}
