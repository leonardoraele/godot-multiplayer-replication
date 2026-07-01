using System;
using Godot;
using Raele.GodotUtils.Extensions;

namespace Raele.MultiplayerReplication;

public partial class MultiplayerInstantiator : Node
{
	// -----------------------------------------------------------------------------------------------------------------
	// STATICS
	// -----------------------------------------------------------------------------------------------------------------

	private const string GROUP_SPAWNED = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(MultiplayerInstantiator)}.{nameof(GROUP_SPAWNED)}";

	public static bool IsNetworkSpawned(Node node)
		=> node.Owner.IsInGroup(GROUP_SPAWNED);
	public static void SetNetworkSpawned(Node node, bool value = true)
	{
		if (value)
			node.Owner.AddToGroup(GROUP_SPAWNED);
		else
			node.Owner.RemoveFromGroup(GROUP_SPAWNED);
	}

	// -----------------------------------------------------------------------------------------------------------------
	// EXPORTS
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Whenever a child node is added to one of the nodes in this list, it will be automatically replicated to the
	/// other peers -- meaning they will also instantiate the same scene and add it to this parent node. This is useful
	/// for synchronizing dynamically spawned objects, such as bullets, enemies, or other objects that are not part of
	/// the initial scene tree.
	///
	/// Likewise, whenever a child node is removed from one of these spawn parents, it will be automatically despawned
	/// on the other peers.
	/// </summary>
	[Export] public Node?[] SpawnParents
	{
		get;
		set
		{
			this.StopObservingSpawnParents();
			field = value;
			if (this.Owner.IsNodeReady())
				this.StartObservingSpawnParents();
			else
				this.Owner.Connect(Node.SignalName.Ready, Callable.From(this.StartObservingSpawnParents), (uint) ConnectFlags.OneShot);
		}
	} = [];

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

	// private enum Type {
	// 	Value1,
	// }

	// -----------------------------------------------------------------------------------------------------------------
	// GODOT EVENTS
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

	// public override void _EnterTree()
	// {
	// 	base._EnterTree();
	// }

	// public override void _ExitTree()
	// {
	// 	base._ExitTree();
	// }

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
	private void StartObservingSpawnParents()
	{
		foreach (Node parent in this.SpawnParents.WhereNotNull())
		{
			parent.ChildEnteredTree += this.OnChildEnteredSpawnParent;
			parent.ChildExitingTree += this.OnChildExitingSpawnParent;
			parent.ChildOrderChanged += this.OnChildOrderChangedInSpawnParent;
		}
	}

	private void StopObservingSpawnParents()
	{
		foreach (Node parent in this.SpawnParents.WhereNotNull())
		{
			parent.ChildEnteredTree -= this.OnChildEnteredSpawnParent;
			parent.ChildExitingTree -= this.OnChildExitingSpawnParent;
			parent.ChildOrderChanged -= this.OnChildOrderChangedInSpawnParent;
		}
	}

	private void OnChildEnteredSpawnParent(Node child)
	{
		if (
			!this.IsMultiplayerAuthority()
			|| child.IsInGroup(GROUP_SPAWNED)
			|| string.IsNullOrWhiteSpace(child.SceneFilePath)
			|| !this.SceneSpawnWhitelist.IsEmpty()
				&& !this.SceneSpawnWhitelist.Contains(child.SceneFilePath)
		)
			return;
		child.AddToGroup(GROUP_SPAWNED);
		this.EmitSignal(SignalName.SpawnChild, new SpawnData()
		{
			ReplicatorId = this.NetworkId,
			SceneUid = ResourceUid.PathToUid(child.SceneFilePath),
			SpawnParentIndex = (byte) this.SpawnParents.IndexOf(child.GetParent()),
		});
	}

	private void OnChildExitingSpawnParent(Node child)
	{
		if (!this.IsMultiplayerAuthority() || !child.IsInGroup(GROUP_SPAWNED))
			return;
		child.RemoveFromGroup(GROUP_SPAWNED);
		if (this.DisableAutomaticDespawn || !ReplicationManager.TryGetNetworkId(child, out Guid networkId))
			return;
		this.EmitSignal(SignalName.DespawnChild, new DespawnData() { NetworkId = networkId });
	}
}
