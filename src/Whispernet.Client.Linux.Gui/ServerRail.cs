using System.Diagnostics;
using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Daemon.Client;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux.Gui;

public sealed partial class WhispernetGuiWindow
{
    private Element BuildServerRailColumn()
    {
        _serverRailIconsStack = new StackPanel()
            .Vertical()
            .Spacing(10);

        _homeRailBtn = new Button()
            .Content("W")
            .Size(48, 48)
            .Background(ColBlurple)
            .Foreground(ColTextHeader)
            .FontSize(18)
            .Bold()
            .CornerRadius(16)
            .ToolTip("Whisper Home (Direct Messages)")
            .OnClick(() => SwitchActiveGroup("general", "Whisper Home"));

        var divider = new Border()
            .Height(2)
            .Width(32)
            .Background(ColChannelActive)
            .CenterHorizontal();

        var addGroupButton = new Button()
            .Content("+")
            .Size(48, 48)
            .Background(ColChatBg)
            .Foreground(ColGreen)
            .FontSize(22)
            .Bold()
            .CornerRadius(24)
            .ToolTip("Create or Join a Governed Group")
            .OnClick(OpenGroupModal);

        var railStack = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Padding(12, 14)
            .Children(
                _homeRailBtn.CenterHorizontal(),
                divider,
                _serverRailIconsStack,
                addGroupButton.CenterHorizontal()
            );

        return new Border()
            .Background(ColServerRail)
            .Child(
                new ScrollViewer()
                    .AutoVerticalScroll()
                    .NoHorizontalScroll()
                    .Content(railStack)
            );
    }

    // 2. COLONNE SALONS & GROUPES (256px)
}
