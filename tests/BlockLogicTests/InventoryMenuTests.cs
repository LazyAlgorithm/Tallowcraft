using System;
using WorldGeneration;

internal static class InventoryMenuTests
{
    public static void Run(Action<bool, string> check)
    {
        var menu = new InventoryMenuState();
        check(!menu.Open && !menu.AwaitingServer,
            "Menu starts closed, with gameplay look enabled");

        check(menu.Update(false, true, true, 0.016f, out bool requested) &&
            requested && menu.Open && menu.AwaitingServer,
            "Tab opens locally without waiting for server replication");

        // A still-held Tab must not close the menu on the next tick.
        for (int frame = 0; frame < 8; frame++)
            check(!menu.Update(false, true, true, 0.016f, out _) &&
                menu.Open, "Held Tab produces only one toggle request");

        // The stale closed server field must not override local focus while
        // RPC/Sync packets travel over the network.
        check(!menu.Update(false, false, false, 0.016f, out _) &&
            menu.Open && menu.AwaitingServer,
            "Local cursor and look focus remain stable before server ack");
        check(!menu.Update(true, false, false, 0.016f, out _) &&
            menu.Open && !menu.AwaitingServer,
            "Server acknowledgement confirms the open inventory");

        check(menu.Update(true, true, true, 0.016f, out requested) &&
            !requested && !menu.Open && menu.AwaitingServer,
            "Second Tab closes immediately without applying menu mouse look");
        check(!menu.Update(true, false, false, 0.016f, out _) &&
            !menu.Open && menu.AwaitingServer,
            "Old replicated open state cannot force the menu open again");
        check(!menu.Update(false, false, false, 0.016f, out _) &&
            !menu.Open && !menu.AwaitingServer,
            "Close acknowledgement completes the request");

        // The Close button uses the same path as Tab, even when gameplay
        // controls are disabled.
        check(menu.Update(false, true, true, 0.016f, out requested) &&
            requested && menu.Open, "A released Tab can reopen the menu");
        check(menu.RequestClose() && !menu.Open &&
            !menu.RequestClose(), "Close button is idempotent");

        var timeout = new InventoryMenuState();
        check(timeout.Update(false, true, true, 0f, out _) &&
            timeout.Open, "Initial open request is pending");
        for (int i = 0; i < 22; i++)
            timeout.Update(false, false, false, 0.25f, out _);
        check(!timeout.Open && !timeout.AwaitingServer,
            "A missing server response restores authoritative input focus");

        check(timeout.Update(false, true, true, 0.016f, out _) &&
            timeout.Open, "Tab can reopen after a timed-out RPC");
        Console.WriteLine(
            "PASS: immediate local inventory focus, key debouncing, RPC ack and timeout");
    }
}
