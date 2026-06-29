#if TOOLS
using Godot;

namespace Raele.MultiplayerReplication;

[Tool]
public partial class MultiplayerReplicationPlugin : EditorPlugin
{
    public override void _EnterTree()
    {
        this.AddAutoloadSingleton(nameof(ReplicationManager), $"res://addons/{nameof(MultiplayerReplication)}/src/{nameof(ReplicationManager)}.cs");
    }
	public override void _ExitTree()
	{
		base._ExitTree();
        this.RemoveAutoloadSingleton(nameof(ReplicationManager));
	}
}
#endif
