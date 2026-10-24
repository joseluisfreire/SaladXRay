#nullable disable
#pragma warning disable CA1416

using Spectre.Console;
using Spectre.Console.Rendering;
using System.Collections.Generic;

namespace SaladXRayPanel
{
    partial class Program
    {
        static IRenderable RenderHelpScreen()
        {
            var helpGrid = new Grid().Expand();
            helpGrid.AddColumn(new GridColumn());

            var fontToUse = Program.GetFont("drpepper") ?? FigletFont.Default;
			var logo = new FigletText(fontToUse, "Salad XRay").Color(Color.SpringGreen3);



			helpGrid.AddRow(new Align(logo, HorizontalAlignment.Center));
            helpGrid.AddRow(new Text(""));
			
            var helpText = new Markup(
                "[yellow][[CTRL]] [[+ / -]][/] : Zoom in/out on the terminal (Windows default).\n" +
                "[yellow][[W]][/]            : Cycle wallet/graph screens.\n" +
                "[yellow][[N]][/]            : Toggle Novatech demand overlay / view.\n" +
                "[yellow][[E]][/]            : Toggle recent events / Error Log panel.\n" +
                "[yellow][[C]][/]            : Clear error log history.\n" +
                "[yellow][[P]][/]            : Toggle Live CPU Clock (real-time, uses extra ~5-7mb RAM while active).\n" +
                "[yellow][[H]][/]            : Toggle between Dashboard and this Help screen.\n" +
                "[yellow][[ESC]][/]          : Safely exit SaladXRay."
            );

            var helpPanel = new Panel(helpText)
                .Header("[white bold] HELP & CONTROLS [/]", Justify.Left)
                .BorderColor(Color.Green)
                .Padding(2, 1, 2, 1);

			helpGrid.AddRow(new Align(helpPanel, HorizontalAlignment.Center));

            string warningIcon = AdaptIcon("⚠️");
            var supportText = new Markup(
                $"[bold yellow]{warningIcon} Don't Panic Over Isolated Errors & Red Logs![/]\n" +
                "Containers, distributed networks, and WSL constantly produce transient warnings. " +
                "Seeing an [red][[ERR]][/] line in X-Ray does [italic]not[/] necessarily mean your node is broken " +
                "or that you stopped earning.\n\n" +
                "Before interrupting any process, [bold yellow]be patient[/] and allow workloads to stabilize before making hasty decisions. " +
                "Always check your settings whenever possible: if a clean reinstall is performed (uninstall, reboot, and reinstall) " +
                "instead of a direct in-place update, the Salad app resets back to its default settings and everything must be configured again. " +
                "Keep these details in mind before seeking support.\n\n" +
                "[bold cyan]1. Explore the Official Troubleshooting Hub First:[/]\n" +
                "Only if you have already tried everything and still feel something is wrong, head to the official support website and check the [yellow]Troubleshooting[/] section:\n" +
                "   [link=https://support.salad.com]https://support.salad.com[/]\n" +
                "It is very likely you will solve the issue entirely on your own thanks to the excellent guides and tutorials available there.\n\n" +
                "[bold cyan]2. Opening a Support Ticket (Last Resort):[/]\n" +
                "Only open a support ticket as a last resort after trying the troubleshooting guides."
            );

            var supportPanel = new Panel(supportText)
                .Header("[white bold] TROUBLESHOOTING & SUPPORT GUIDELINES [/]", Justify.Left)
                .BorderColor(Color.DarkOrange)
                .Padding(2, 1, 2, 1);

            helpGrid.AddRow(supportPanel);

            var navInstruction = new Markup("\n[blink red][[H]][/] Back to Dashboard");
            helpGrid.AddRow(new Align(navInstruction, HorizontalAlignment.Center));

            return helpGrid;
        }
    }
}
