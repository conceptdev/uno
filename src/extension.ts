import * as vscode from 'vscode';
import * as xamlpreview from './unoplayground';
import * as XamlComplete from './xaml/XamlExt';
import { ExtensionUtils } from './ExtensionUtils';
import which from 'which';
import * as path from 'path';
import { UnoPlatformCmdProvider } from './UnoPlatformCmdProvider';
import { UnoCsprojManager } from './UnoCsprojManager';
import { UnoNewProjectManager } from './UnoNewProjectManager';

export function activate (context: vscode.ExtensionContext): void {
    // load the xaml contributions
    XamlComplete.XamlActivate(context);

    // now try to load the uno playground xaml preview
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'unoplatform.xamlPreview',
            xamlpreview.createXAMLPreview
        )
    );

    ExtensionUtils.showProgress("Initializing Uno Platform Ext ...", "",
        async (res, pro): Promise<void> => {
            // check dotnet
            var dotnetPath: string | undefined;
            await which("dotnet", (err, path) => {
                if (err === null) {
                    dotnetPath = path;
                }
            });

            // TODO: it would be nice to have some way to update it through CI
            // artifacts instead of distributing it in the repository
            ExtensionUtils.createTerminal(context, "HotReload Server",
                path.join(context.extensionPath, "uno-remote-host"), dotnetPath,
                [
                    "Uno.UI.RemoteControl.Host.dll",
                    // TODO: make this configurable
                    "--httpPort=8090"
                ]
            );

            // register the commands
            UnoCsprojManager.Register();
            UnoNewProjectManager.Register(context);

            // ok now create the activity bar view content
            const cmdNodesProvider = new UnoPlatformCmdProvider();
            vscode.window.registerTreeDataProvider("unoDevCmdView", cmdNodesProvider);

            res();
        });

    // all ok?
    console.log('Uno Platform extension loaded');
}

// this method is called when your extension is deactivated
export function deactivate (): void { }
