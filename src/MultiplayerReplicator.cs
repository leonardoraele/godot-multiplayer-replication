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

	public MultiplayerReplicator()
		// We generate and assign a new ID to the replicator in the constructor. This is useful when the node is
		// initially added by the developer in the godot editor. This id is read by the SerializedNetworkId property,
		// which is an export field that is saved to the scene file. When the scene is loaded, this constructor
		// generates a new id, but it's immediately overriden by the id saved in the scene. This strategy ensures a
		// replicator always has the same id for every peer.
		=> NetworkIdManager.SetNewId(this);

	// -----------------------------------------------------------------------------------------------------------------
	// EXPORTS
	// -----------------------------------------------------------------------------------------------------------------

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

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Stores the values of the replicated fields in the same order as the fields are registered in the
	/// <see cref="ReplicatedFields"/> array. Updated every frame.
	/// </summary>
	private ReplicatedFieldHelper[] ReplicatedFieldHelpers
	{
		get
		{
			if (field == null || this.ReplicatedFields.Count != field.Length)
			{
				field = new ReplicatedFieldHelper[this.ReplicatedFields.Count];
				for (int i = 0; i < this.ReplicatedFields.Count; i++)
				{
					if (!this.GetFieldDataAt(i, out Node? subject, out string? fieldName))
						continue;
					field[i] = new ReplicatedFieldHelper { Target = subject, FieldName = fieldName };
				}
			}
			return field;
		}
	}

	/// <summary>
	/// Unique identifier for this <see cref="MultiplayerReplicator"/> across the network.
	/// </summary>
	[Obsolete("Use NetworkIdManager.GetId() and NetworkIdManager.SetId() instead.")]
	public Guid NetworkId
	{
		get => NetworkIdManager.GetId(this);
		set => NetworkIdManager.SetId(this, value);
	}

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// This node's <see cref="NetworkId"/> as a string. This is useful as it can be serialized by godot and stored in
	/// the scene. This export field is storage-only and hidden in the editor.
	/// </summary>
	[Export] public string SerializedNetworkId
	{
		get => this.NetworkId.ToString();
		set
		{
			Debug.Assert(Guid.TryParse(value, out Guid parsed), $"Failed to parse {nameof(this.SerializedNetworkId)}. Cause: Invalid GUID string. Value: \"{value}\"");
			this.NetworkId = parsed;
		}
	}

	// -----------------------------------------------------------------------------------------------------------------
	// SIGNALS
	// -----------------------------------------------------------------------------------------------------------------

	[Signal] public delegate void ReplicationDataEventHandler(ReplicationData data);
	[Signal] public delegate void SpawnChildEventHandler(SpawnData data);
	[Signal] public delegate void DespawnChildEventHandler(DespawnData data);

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

	/// <summary>
	/// Helper struct to store the old and new values of a replicated field, along with the target node and field name.
	/// This is used to detect when a field has changed its value and needs to be replicated to the other peers.
	/// </summary>
	private struct ReplicatedFieldHelper
	{
		public required Node Target { get; init; }
		public required string FieldName { get; init; }
		public Variant OldValue { get; private set; }
		public Tween? InterpolationTween { get; set; }

		public Variant CurrentValue
		{
			get => this.Target.GetIndexed(this.FieldName);
			set => this.Target.SetIndexed(this.FieldName, value);
		}
		public bool IsDirty => !this.CurrentValue.Equals(this.OldValue);

		public void Cache()
			=> this.OldValue = this.CurrentValue;
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
		}
	}

	public override string[] _GetConfigurationWarnings()
	{
		List<string> warnings = [.. base._GetConfigurationWarnings() ?? []];
		foreach (string field in this.ReplicatedFields)
			if (!this.GetFieldData(field, out _, out _))
				warnings.Add($"The field \"{field}\" is invalid. See console log for details.");
		return warnings.ToArray();
	}

	public override void _EnterTree()
	{
		base._EnterTree();
		if (Engine.IsEditorHint())
			return;
		if (MultiplayerInstantiator.IsNetworkSpawned(this))
			NetworkIdManager.SetNewId(this);
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

	/// <summary>
	/// This method is called by the <see cref="ReplicationManager"/> every frame, but it only sends replication data to
	/// the peers if the replication strategy is not set to <see cref="ReplicationStrategyEnum.ReliablyOnDemand"/>.
	/// </summary>
	private void ProcessNetwork()
	{
		if (this.ReplicationStrategy != ReplicationStrategyEnum.ReliablyOnDemand)
			this.ForceReplicate();
	}

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTY REPLICATION METHODS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Forces immediate replication of the current values of replicated fields to the other peers, regardless of
	/// whether they have changed or not.
	///
	/// This method is particularly useful when <see cref="ReplicationStrategy"/> is set to
	/// <see cref="ReplicationStrategyEnum.ReliablyOnDemand"/>. If this is the case, this method must be called manually
	/// by the user whenever they want to replicate the current values of the observed fields to the other peers.
	///
	/// No data is sent if the local host is not the multiplayer authority for this node. The multiplayer authority peer
	/// is the only peer that should send replication data to the others.
	/// </summary>
	public void ForceReplicate()
	{
		if (!this.IsMultiplayerAuthority())
			return;
		ReplicationData? data = this.ReplicationStrategy == ReplicationStrategyEnum.ReliablyOnChange
			? this.BuildReplicationDataWithDirtyFields()
			: this.BuildReplicationDataWithAllFields();
		if (data != null)
			this.EmitSignal(SignalName.ReplicationData, data);
		this.ReplicatedFieldHelpers.ForEach(helper => helper.Cache());
	}

	/// <summary>
	/// Builds a <see cref="ReplicationData"/> packet with the most recent data for all replicated fields. This method
	/// returns null if there are no replicated fields. (i.e. if <see cref="ReplicatedFields"/> is empty)
	/// </summary>
	private ReplicationData? BuildReplicationDataWithAllFields()
	{
		uint dirtyFields = 0u.BitFill(0, this.ReplicatedFieldHelpers.Length);
		if (!dirtyFields.HasAnyBitSet())
			return null;
		Variant[] values = this.ReplicatedFieldHelpers.Select(helper => helper.CurrentValue).ToArray();
		return new(this.NetworkId, dirtyFields, values);
	}

	/// <summary>
	/// Gets a <see cref="ReplicationData"/> packet with the most recent value for the fields that have changed since
	/// the last replication. This method returns null if there are no dirty fields.
	/// </summary>
	private ReplicationData? BuildReplicationDataWithDirtyFields()
	{
		uint dirtyFields = this.ReplicatedFieldHelpers.Select((helper, index) => (helper, index))
			.Where(tuple => tuple.helper.IsDirty)
			.Aggregate(0u, (acc, tuple) => acc.SetBit(tuple.index));
		if (!dirtyFields.HasAnyBitSet())
			return null;
		Variant[] newValues = this.ReplicatedFieldHelpers
			.Where(helper => helper.IsDirty)
			.Select(helper => helper.CurrentValue)
			.ToArray();
		return new(this.NetworkId, dirtyFields, newValues);
	}

	/// <summary>
	/// This method is used by the <see cref="ReplicationManager"/> to deliver replication data received from the
	/// multiplayer authority peer. It updates the corresponding fields on the local host.
	/// </summary>
	public void AcceptReplicationData(ReplicationData data)
		=> this.ReplicatedFieldHelpers.Where((_, index) => data.FieldMask.HasBitSet(index))
			.ForEach((helper, index) => this.AcceptNewFieldValue(helper, data.Values[index]));

	private void AcceptNewFieldValue(ReplicatedFieldHelper helper, Variant newValue)
	{
		if (this.InterpolateValues)
			this.AcceptNewFieldValueWithInterpolation(helper, newValue);
		else
			this.AcceptNewFieldValueWithoutInterpolation(helper, newValue);
	}

	private void AcceptNewFieldValueWithoutInterpolation(ReplicatedFieldHelper helper, Variant newValue)
		=> helper.CurrentValue = newValue;

	private void AcceptNewFieldValueWithInterpolation(ReplicatedFieldHelper helper, Variant newValue)
	{
		double duration = ReplicationManager.Instance.GetReplicationInterpolationTime(this).TotalSeconds;
		Callable setter = Callable.From((Variant intermediateValue) => helper.CurrentValue = intermediateValue);
		helper.InterpolationTween?.Kill();
		helper.InterpolationTween = this.CreateTween();
		helper.InterpolationTween.TweenMethod(setter, helper.CurrentValue, newValue, duration);
	}

	// -----------------------------------------------------------------------------------------------------------------
	// INTEREST METHODS
	// -----------------------------------------------------------------------------------------------------------------

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

	// -----------------------------------------------------------------------------------------------------------------
	// OTHER METHODS
	// -----------------------------------------------------------------------------------------------------------------

	private void OnAddReplicationFieldButtonPressed()
	{
		EditorInterface.Singleton.PopupNodeSelector(Callable.From((NodePath path) =>
		{
			Node subject = this.Owner.GetNode(path);
			EditorInterface.Singleton.PopupPropertySelector(subject, Callable.From((string propertyPath) =>
			{
				this.ReplicatedFields.Add(this.Owner.GetPathTo(subject) + propertyPath);
				this.NotifyPropertyListChanged();
			}));
		}));
	}

	private bool GetFieldDataAt(
		int index,
		[NotNullWhen(true)] out Node? subject,
		[NotNullWhen(true)] out string? fieldName
	)
		=> this.GetFieldData(this.ReplicatedFields[index], out subject, out fieldName);
	private bool GetFieldData(
		string fieldPath,
		[NotNullWhen(true)] out Node? subject,
		[NotNullWhen(true)] out string? fieldName
	)
	{
		subject = null;
		(string? nodePath, fieldName) = fieldPath.Split("::");
		if (string.IsNullOrWhiteSpace(nodePath) || string.IsNullOrWhiteSpace(fieldName))
		{
			GD.PushWarning($"{nameof(MultiplayerReplicator)}: Failed to get value for field \"{fieldPath}\". Cause: it should be in the format \"Path/To/Node:property_name\".");
			return false;
		}
		subject = this.Owner.GetNode(nodePath);
		if (subject == null)
		{
			GD.PushWarning($"{nameof(MultiplayerReplicator)}: Failed to get value for field \"{fieldPath}\". Cause: the node at path \"{nodePath}\" does not exist.");
			return false;
		}
		return true;
	}
}
