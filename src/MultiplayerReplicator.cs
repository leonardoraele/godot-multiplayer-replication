using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

	public const string GROUP_NAME = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(MultiplayerReplicator)}";

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
	/// The unique identifier for this MultiplayerReplicator. This ID is used to identify the replicated object across
	/// the network. It is generated when the MultiplayerReplicator is created and remains constant throughout its
	/// lifetime.
	/// </summary>
	/// <remarks>
	/// This field is exported so that it can be stored in the scene file and remain constant across game sessions; but
	/// it is hidden in the editor and automatically generated when the MultiplayerReplicator is created. It should not
	/// be modified by the user.
	/// </remarks>
	[Export] private string SerializedNetworkId
	{
		get => this.NetworkId.ToString();
		set => this.NetworkId = Guid.TryParse(value, out Guid guid) ? guid : Guid.Empty;
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
		.GetNodesInGroup(GROUP_NAME)
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

	// [Signal] public delegate void EventHandler()

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
		/// Replication data is sent to the peers only when the the <see cref="Refresh"/> method is called by the user.
		///
		/// This is useful for properties that are usually not relevant to the other peers, but that can be replicated
		/// on demand. For example, a player can request to see the inventory of another player, and the inventory can
		/// be replicated only when the request is made.
		/// </summary>
		ManuallyOnDemand,
	}

	// -----------------------------------------------------------------------------------------------------------------
	// EVENTS
	// -----------------------------------------------------------------------------------------------------------------

	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		base._ValidateProperty(property);
		switch (property["name"].AsString()) {
			case nameof(this.SerializedNetworkId):
				property["usage"] = (long) PropertyUsageFlags.Storage;
				break;
			case nameof(this.SceneSpawnWhitelist):
				property["type"] = (long) Variant.Type.Array;
				property["hint"] = (long) PropertyHint.ArrayType;
				property["hint_string"] = $"{Variant.Type.String:D}/{PropertyHint.File:D}:*.tscn,*.scn";
				break;
		}
	}

	public override string[] _GetConfigurationWarnings()
		=> (base._GetConfigurationWarnings() ?? [])
			.AppendIf(this.Root == null, $"The mandatory field '{nameof(this.Root)}' is not set.")
			// TODO Detect if there are any invalid entries in the replicated fields list
			.AppendIf(!this.UniqueNameInOwner, $"The {nameof(MultiplayerReplicator)} node must have a unique name. This is necessary to facilitate identifying it across the network. Please right-click this node and enable \"Access as Unique Name\" for this node.")
			.ToArray();

	public override void _EnterTree()
	{
		base._EnterTree();
		this.AddToGroup(GROUP_NAME, persistent: true);
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

	[Obsolete]
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
	public bool BuildNextReplicationData([NotNullWhen(true)] out ReplicationData? data)
	{
		data = null;
		if (!this.IsMultiplayerAuthority())
			return false;
		uint dirtyFields = this.GetNextDirtyFields();
		if (dirtyFields == 0u)
			return false;
		data = new(this.NetworkId, dirtyFields, [..this.ReplicatedFieldsCache]);
		// GD.PrintS(nameof(MultiplayerReplicator), this.Multiplayer.GetUniqueId(), $"Generated replication data for \"{this.Target.Name}\" ({string.Join(", ", this.GetFieldNames(data.FieldMask))})");
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
		if (!this.IsMultiplayerAuthority() || this.Root == null)
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
		if (this.Root == null)
			return false;
		string fieldName = this.ReplicatedFields[index];
		Variant currentValue = this.Root.GetIndexed(fieldName);
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
				(string? nodePath, string? fieldName) = fieldPath.Split("::");
				if (string.IsNullOrWhiteSpace(nodePath) || string.IsNullOrWhiteSpace(fieldName))
					return fieldPath;
				Node? subject = oldTarget.GetNode(nodePath);
				if (subject == null)
					return fieldPath;
				return $"{newTarget.GetPathTo(subject)}::{fieldName}";
			})
			.ToGodotArrayT();
		this.NotifyPropertyListChanged();
	}
}
