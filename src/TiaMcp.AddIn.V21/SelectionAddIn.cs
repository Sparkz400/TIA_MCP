using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;

namespace TiaMcp.AddIn
{
    public sealed class SelectionAddInProvider : ProjectTreeAddInProvider
    {
        private readonly TiaPortal _portal;
        public SelectionAddInProvider(TiaPortal portal) { _portal = portal; }
        protected override IEnumerable<ContextMenuAddIn> GetContextMenuAddIns() { yield return new SelectionAddIn(_portal); }
    }

    public sealed class SelectionAddIn : ContextMenuAddIn
    {
        private readonly TiaPortal _portal;
        public SelectionAddIn(TiaPortal portal) : base("TIA MCP") { _portal = portal; }

        // The selection count is internal to the API, so Send enforces the 1..64 bound itself.
        protected override void BuildContextMenuItems(ContextMenuAddInRoot addInRoot) =>
            addInRoot.Items.AddActionItem<IEngineeringObject>("Send selection to TIA MCP", Send);

        private void Send(MenuSelectionProvider<IEngineeringObject> selection)
        {
            NotificationIcon icon; string message, detail;
            try { detail = SelectionHandoff.Send(selection.GetSelection<IEngineeringObject>()); icon = NotificationIcon.Success; message = "Selection handed to TIA MCP."; }
            // A throwing click handler would end the Add-In; report every refusal instead.
            catch (Exception error) { detail = error.Message; icon = NotificationIcon.Error; message = "Selection was not handed to TIA MCP."; }
            // No other UI channel exists; the Host retains any admitted request whether or not this shows.
            try { _portal.GetService<MessageBoxProvider>()?.ShowNotification(icon, "TIA MCP", message, detail); } catch (Exception) { }
        }
    }
}
