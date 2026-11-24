using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Services;

namespace Berichtsheft.Desktop;

/// <summary>Checked list of all Berufsschule subjects (Lernfelder LF1–LF12 of every Fachrichtung and any other Fach),
/// with quick buttons for the Lernfelder of an Ausbildungsjahr.</summary>
internal sealed class LernfeldList : Panel
{
    private readonly CheckedListBox _list = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly List<DepartmentOut> _subjects;
    private readonly Curriculum.Specialization? _spec;

    public LernfeldList(List<DepartmentOut> subjects, Curriculum.Specialization? spec, int height = 150)
    {
        _subjects = subjects;
        _spec = spec;
        Height = height;
        foreach (var s in subjects) _list.Items.Add(Curriculum.Label(s));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Margin = new Padding(0), Padding = new Padding(0, 2, 0, 0) };
        foreach (var year in new[] { 1, 2, 3 })
            bar.Controls.Add(Small($"{year}. Jahr", () => Check(Curriculum.ForYear(_subjects, year, _spec).Select(d => d.Id))));
        bar.Controls.Add(Small("Alle", () => Check(_subjects.Select(d => d.Id))));
        bar.Controls.Add(Small("Keine", () => Check(Array.Empty<int>())));
        Controls.Add(_list);
        Controls.Add(bar);
    }

    private static Button Small(string text, Action a)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 23, Margin = new Padding(0, 0, 4, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    public void Check(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        for (var i = 0; i < _subjects.Count; i++) _list.SetItemChecked(i, set.Contains(_subjects[i].Id));
    }

    public List<int> CheckedIds => _list.CheckedIndices.Cast<int>().Select(i => _subjects[i].Id).ToList();

    public new bool Enabled { get => _list.Enabled; set { _list.Enabled = value; foreach (Control c in Controls) c.Enabled = value; } }
}

internal static class LernfeldPicker
{
    /// <summary>Asks for the subjects of a school day. Returns null when cancelled or nothing was chosen.</summary>
    public static List<int>? Pick(IWin32Window owner, string title, List<DepartmentOut> subjects, Curriculum.Specialization? spec, IEnumerable<int> preselected)
    {
        if (subjects.Count == 0) { Ui.Info("Es sind keine Lernfelder angelegt (Extras > Themen und Textbausteine)."); return null; }
        var list = new LernfeldList(subjects, spec, 260);
        list.Check(preselected);
        var hint = new Label { Text = "Die Themen des Tages werden aus den gewählten Lernfeldern erzeugt.", AutoSize = true, ForeColor = SystemColors.GrayText };
        var content = Ui.Form(("Lernfelder:", list), ("", hint));
        List<int>? result = null;
        using var dlg = new EditDialog(title, 560, content, () =>
        {
            if (list.CheckedIds.Count == 0) { Ui.Info("Bitte mindestens ein Lernfeld auswählen."); return false; }
            result = list.CheckedIds;
            return true;
        });
        return dlg.ShowDialog(owner) == DialogResult.OK ? result : null;
    }
}
