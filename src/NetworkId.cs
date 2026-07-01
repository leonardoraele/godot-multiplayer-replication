using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Raele.MultiplayerReplication;

/// <summary>
/// This utility class provides methods for assigning and retrieving network identifiers to/from nodes. The ID is
/// assigned to the node as a metadata property. This is useful as it allow us to assign IDs to arbitrary nodes
/// regardless of their types, and without requiring them to implement any specific interface or inherit from a class.
///
/// It also manages a registry table with all nodes in the scene with network IDs, allowing for quick lookups of nodes
/// by their network IDs.
/// </summary>
public static class NetworkIdManager
{
	// -----------------------------------------------------------------------------------------------------------------
	// STATICS
	// -----------------------------------------------------------------------------------------------------------------

	private const string META_NETWORK_ID = $"{nameof(Raele)}.{nameof(MultiplayerReplication)}.{nameof(NetworkIdManager)}";

	// -----------------------------------------------------------------------------------------------------------------
	// FIELDS
	// -----------------------------------------------------------------------------------------------------------------

	private static readonly Dictionary<Guid, WeakReference<Node>> Registry = new();

	// -----------------------------------------------------------------------------------------------------------------
	// PROPERTIES
	// -----------------------------------------------------------------------------------------------------------------

	// -----------------------------------------------------------------------------------------------------------------
	// METHODS
	// -----------------------------------------------------------------------------------------------------------------

	public static bool TryGetId(Node child, out Guid id)
	{
		id = child.HasMeta(META_NETWORK_ID)
			? new Guid(child.GetMeta(META_NETWORK_ID).AsByteArray())
			: Guid.Empty;
		return id != Guid.Empty;
	}
	public static Guid GetId(Node child)
		=> TryGetId(child, out Guid id) ? id : Guid.Empty;
	public static void SetNewId(Node child)
		=> SetId(child, Guid.NewGuid());
	public static void SetId(Node child, Guid id)
	{
		RemoveId(child);
		child.SetMeta(META_NETWORK_ID, id.ToByteArray());
		Registry[id] = new WeakReference<Node>(child);
	}
	public static void RemoveId(Node child)
	{
		if (TryGetId(child, out Guid id))
			Registry.Remove(id);
		child.RemoveMeta(META_NETWORK_ID);
	}

	public static Node? GetNodeOrNull(Guid id)
	{
		if (!Registry.TryGetValue(id, out WeakReference<Node>? box))
			return null;
		if (!box.TryGetTarget(out Node? target))
		{
			Registry.Remove(id);
			return null;
		}
		return target;
	}
	public static bool TryGetNode(Guid id, out Node? node)
	{
		node = GetNodeOrNull(id);
		return node != null;
	}
	public static IEnumerable<Node> GetAllNodes()
	{
		foreach ((Guid guid, WeakReference<Node> box) in Registry)
			if (box.TryGetTarget(out Node? node))
				yield return node;
			else
				Registry.Remove(guid);
	}

	public static void Defragment()
		=> GetAllNodes().ToList(); // This will force the enumeration and remove dead references from the registry
}
