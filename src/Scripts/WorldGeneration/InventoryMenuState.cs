using System;

namespace WorldGeneration;

/// <summary>
/// Local-only focus gate for the inventory. The client immediately hides
/// gameplay controls when a menu request is sent, rather than waiting for a
/// round trip to the server's [Sync] InventoryOpen acknowledgement.
/// </summary>
public sealed class InventoryMenuState
{
    public bool Open { get; private set; }
    public bool AwaitingServer { get; private set; }

    private bool _keyReleased = true;
    private float _ackWait;

    /// <summary>
    /// Called once per local frame with the server's last replicated state.
    /// Return true only for a new menu-toggle request. The caller must send
    /// the returned desired state to the server via the owner-only RPC.
    /// </summary>
    public bool Update(bool serverOpen, bool togglePressed, bool toggleHeld,
        float deltaTime, out bool requestedOpen)
    {
        if (AwaitingServer)
        {
            if (serverOpen == Open)
                AwaitingServer = false;
            else
            {
                _ackWait += Math.Clamp(deltaTime, 0f, 0.25f);
                if (_ackWait >= 5f)
                {
                    // A disconnected/rejected request must not lock the UI forever.
                    Open = serverOpen;
                    AwaitingServer = false;
                }
            }
        }
        else
            Open = serverOpen;

        if (!toggleHeld)
            _keyReleased = true;

        if (togglePressed && _keyReleased)
        {
            _keyReleased = false;
            SetDesired(!Open);
            requestedOpen = Open;
            return true;
        }

        requestedOpen = Open;
        return false;
    }

    public bool RequestClose()
    {
        if (!Open)
            return false;
        SetDesired(false);
        return true;
    }

    private void SetDesired(bool open)
    {
        Open = open;
        AwaitingServer = true;
        _ackWait = 0f;
    }
}
