#if TOOLS
using Godot;

namespace BulletScript.EditorTools;

// Registers a "BulletScript" tab next to 2D / 3D / Script in the Godot editor.
[Tool]
public partial class BulletScriptPlugin : EditorPlugin
{
    EditorMain _main;

    public override void _EnterTree()
    {
        _main = new EditorMain { Name = "BulletScriptEditor" };
        _main.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _main.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(_main);
        _MakeVisible(false);
    }

    public override void _ExitTree()
    {
        _main?.QueueFree();
        _main = null;
    }

    public override bool _HasMainScreen() => true;

    public override void _MakeVisible(bool visible)
    {
        if (_main != null) _main.Visible = visible;
    }

    public override string _GetPluginName() => "BulletScript";

    public override Texture2D _GetPluginIcon()
        => EditorInterface.Singleton.GetEditorTheme().GetIcon("Script", "EditorIcons");
}
#endif
