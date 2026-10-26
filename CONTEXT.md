# OmniDesk

Lets a person move between the Windows accounts they are signed in to using the same Ctrl+Win+Left/Right keys they use to move between virtual desktops.

## Language

**Session**:
A signed-in account's running Windows session. Only sessions that exist can be swapped to.
_Avoid_: Login, user (when meaning the running session)

**Console**:
The session currently attached to the physical screen and keyboard.
_Avoid_: Foreground user, active user

**Swap**:
Moving the Console to a different Session, landing on that Session's lock screen.
_Avoid_: Switch user, account switch (these mean the Windows user-picker flow)

**Swap Ring**:
The ordered list of live Sessions that Left and Right move through. Accounts without a live Session are not in it.
_Avoid_: Account list, user list

**Edge**:
The first or last virtual desktop of the current Session. Pressing past an Edge triggers a Swap.
_Avoid_: Boundary, end desktop

**Arrival**:
Which virtual desktop the target Session shows after a Swap. By default, wherever it was left; optionally the nearest Edge desktop in the direction travelled.
_Avoid_: Landing, destination desktop

**Agent**:
The per-Session helper that watches the keys, detects the Edge, and applies Arrival.
_Avoid_: Client, tray app

**Service**:
The privileged helper that performs the Swap on behalf of an Agent.
_Avoid_: Daemon, backend
