#!/usr/bin/env python3
"""
"Cost info" window for the GNOME extension (equivalent to ShowCostInfo).
Reads /tmp/opencode-tray-state.json (or argument) and shows an estimate.
"""
import sys, os, json

try:
    import gi
    gi.require_version('Gtk', '4.0')
    from gi.repository import Gtk, GLib
except Exception:
    try:
        import gtk
    except Exception:
        pass

def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "/tmp/opencode-tray-state.json"
    try:
        with open(path) as f:
            data = json.load(f)
    except Exception as e:
        data = {"error": str(e)}

    # Build base text (similar to BuildCostReport)
    lines = ["OPENCODE COST ESTIMATE", "how much would I pay if the model were not free", ""]
    lines.append("Status: " + str(data.get("state", "?")))
    lines.append("Instance / project: " + str(data.get("project", "?")))
    lines.append("CWD: " + str(data.get("cwd", "?")))
    lines.append("Estimated cost: ~" + str(data.get("cost", 0)) + " $")
    lines.append("")
    if isinstance(data.get("sessions"), list):
        lines.append("Active sessions: " + str(len(data["sessions"])))
        for s in data["sessions"]:
            sid = s.get("id", "?")
            proj = s.get("project", "?")
            state = s.get("state", "?")
            lines.append("  - " + sid + " [" + proj + "] " + state)
    lines.append("")
    lines.append("(Weekly/monthly projections require full token parsing;")
    lines.append("for now shows current state read from listener.)")
    text = "\n".join(lines)

    # Se disponibile GTK 4
    try:
        from gi.repository import Gtk
        win = Gtk.Window(title="opencode - stima costi")
        win.set_default_size(640, 480)
        win.connect("destroy", lambda *_: Gtk.Application.get_default().quit() if Gtk.Application.get_default() else None)
        # In GTK4 usiamo TextView inside ScrolledWindow
        text_view = Gtk.TextView()
        text_view.set_editable(False)
        text_view.get_buffer().set_text(text, -1)
        sw = Gtk.ScrolledWindow()
        sw.set_child(text_view)
        win.set_child(sw)
        win.present()
        # Loop
        app = Gtk.Application(application_id="org.local.opencode.cost")
        def on_activate(app):
            pass
        app.connect("activate", on_activate)
        # Non avviamo il loop se non necessario; semplicemente mostriamo
        # For simplicity we use a direct approach with Gtk.main()
    except Exception as e:
        pass

    # Fallback: stampa su stdout / mostra con zenity o xmessage
    try:
        import subprocess
        # Use zenity for an immediate window
        subprocess.run(["zenity", "--text-info", "--title=opencode - stima costi",
                        "--width=700", "--height=500", "--filename=-"],
                       input=text.encode(), capture_output=True)
    except Exception:
        try:
            # Ultimo attempt: xmessage
            import subprocess
            subprocess.run(["xmessage", "-center", text[:500]])
        except Exception:
            print(text)

if __name__ == "__main__":
    main()
