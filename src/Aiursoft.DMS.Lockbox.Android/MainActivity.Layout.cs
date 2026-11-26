using Aiursoft.DMS.Contracts;
using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Chip;
using Google.Android.Material.TextField;

namespace Aiursoft.DMS.Lockbox.Android;

public sealed partial class MainActivity
{
    private const string Ink = "#172D29";
    private const string Subtle = "#536661";
    private const string Accent = "#174D45";
    private const string Border = "#D8E3E0";
    private const string Paper = "#F2F6F5";

    private enum ActionStyle { Primary, Secondary, Danger }

    private void Page(string title, string subtitle, Action? back = null, string? actionLabel = null, Action? action = null)
    {
        _buttons.Clear();
        _content = Column();
        _content.SetPadding(Dp(20), Dp(14), Dp(20), Dp(24));
        _content.FocusableInTouchMode = true;
        var scroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
        scroll.SetBackgroundColor(Color.ParseColor(Paper));
        if (OperatingSystem.IsAndroidVersionAtLeast(35))
            scroll.SetOnApplyWindowInsetsListener(new PageInsets());
        else
            scroll.SetFitsSystemWindows(true);
        scroll.AddView(_content);
        SetContentView(scroll);
        scroll.RequestApplyInsets();
        _content.RequestFocus();

        var toolbar = Row(_content, gap: 0);
        var badge = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        badge.SetImageResource(Resource.Drawable.ic_lockbox);
        badge.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        badge.Background = Rounded(Accent, 12);
        toolbar.AddView(badge, new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        var wordmark = Text(toolbar, "DMS  /  LOCKBOX", 13, Accent, bold: true, share: true);
        wordmark.SetPadding(Dp(10), 0, 0, 0);
        if (back is not null) ActionButton(toolbar, "返回", back, ActionStyle.Secondary);
        if (actionLabel is not null && action is not null) ActionButton(toolbar, actionLabel, action, ActionStyle.Secondary);

        var hero = Column();
        hero.Background = Rounded(Accent, 22);
        hero.SetPadding(Dp(20), Dp(17), Dp(20), Dp(18));
        Add(_content, hero, top: 12);
        Text(hero, title, 30, "#FFFFFF", bold: true);
        Text(hero, subtitle, 14, "#D7EFE7", top: 5);
        _notice = Text(_content, "", 14, Accent, top: 12);
        _notice.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        _notice.Background = Rounded("#D7EFE7", 12);
        _notice.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        Message("");
    }

    private sealed class PageInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var padding = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
                view.SetPadding(padding.Left, padding.Top, padding.Right, padding.Bottom);
            }
            return insets;
        }
    }

    private LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };

    private LinearLayout Row(LinearLayout parent, int gap = 8)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        Add(parent, row, top: gap);
        return row;
    }

    private MaterialCardView Card(LinearLayout parent, out LinearLayout body, Action? action = null)
    {
        var card = new MaterialCardView(this)
        {
            Radius = Dp(18), CardElevation = 0, StrokeWidth = Dp(1)
        };
        card.SetCardBackgroundColor(Color.White);
        card.SetStrokeColor(Colors(Border));
        body = Column();
        body.SetPadding(Dp(16), Dp(13), Dp(16), Dp(13));
        card.AddView(body, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        if (action is not null)
        {
            card.Clickable = true;
            card.Focusable = true;
            card.Enabled = !_busy;
            body.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
            card.Click += (_, _) => action();
            _buttons.Add(card);
        }
        Add(parent, card, top: 10);
        return card;
    }

    private LinearLayout Section(string heading, string? subtitle = null)
    {
        Card(_content, out var body);
        Text(body, heading, 16, Ink, bold: true);
        if (subtitle is not null) Text(body, subtitle, 13, Subtle, top: 4);
        return body;
    }

    private void Heading(LinearLayout parent, string heading) => Text(parent, heading, 17, Ink, bold: true, top: 15);

    private void Metadata(LinearLayout parent, string key, string value, bool mono = false)
    {
        Text(parent, key, 12, Subtle, bold: true, top: 7);
        Text(parent, value, 14, Ink, top: 1, mono: mono);
    }

    private TextView Text(LinearLayout parent, string value, float size, string color, bool bold = false,
        int top = 0, bool share = false, bool mono = false)
    {
        var label = new TextView(this) { Text = value, TextSize = size };
        label.SetTextColor(Color.ParseColor(color));
        if (bold || mono) label.Typeface = mono ? Typeface.Monospace : Typeface.Create("sans-serif-medium", TypefaceStyle.Normal);
        Add(parent, label, top, share);
        return label;
    }

    private EditText Field(LinearLayout parent, string hint, InputTypes type)
    {
        var box = (TextInputLayout)LayoutInflater.Inflate(Resource.Layout.outlined_field, parent, false)!;
        var field = box.EditText!;
        box.Hint = hint;
        field.InputType = type;
        field.SaveEnabled = false;
        field.SetSingleLine(!type.HasFlag(InputTypes.TextFlagMultiLine));
        if (type == (InputTypes.ClassText | InputTypes.TextVariationPassword))
            field.TransformationMethod = global::Android.Text.Method.PasswordTransformationMethod.Instance;
        Add(parent, box, top: 11);
        return field;
    }

    private void ActionButton(LinearLayout parent, string title, Action action, ActionStyle style = ActionStyle.Primary, bool share = false)
    {
        var button = new MaterialButton(this) { Text = title, Enabled = !_busy, CornerRadius = Dp(13) };
        button.SetAllCaps(false);
        button.SetMinHeight(Dp(48));
        button.SetMinWidth(0);
        button.BackgroundTintList = Colors(style == ActionStyle.Primary ? Accent : "#E6EFEC");
        button.SetTextColor(Colors(style == ActionStyle.Primary ? "#FFFFFF" : style == ActionStyle.Danger ? "#8C2B23" : Accent));
        button.Click += (_, _) => action();
        _buttons.Add(button);
        Add(parent, button, top: parent.Orientation == Orientation.Vertical ? 8 : 0, share: share,
            start: parent.Orientation == Orientation.Horizontal && parent.ChildCount > 0 ? 8 : 0);
    }

    private void RiskChip(LinearLayout parent, RiskLevel risk)
    {
        var (background, foreground) = risk switch
        {
            RiskLevel.High or RiskLevel.VeryHigh => ("#FBE2DC", "#812820"),
            RiskLevel.Medium => ("#FAEBCB", "#6B4909"),
            _ => ("#E4F1EB", "#245447")
        };
        Chip(parent, RiskText(risk) + "风险", background, foreground);
    }

    private void Chip(LinearLayout parent, string value, string background, string foreground)
    {
        var chip = new Chip(this) { Text = value, Clickable = false, Checkable = false, Focusable = false, TextSize = 12 };
        chip.SetTextColor(Color.ParseColor(foreground));
        chip.ChipBackgroundColor = Colors(background);
        chip.ChipStrokeWidth = 0;
        chip.ChipMinHeight = Dp(27);
        chip.SetEnsureMinTouchTargetSize(false);
        Add(parent, chip, start: parent.ChildCount > 0 ? 6 : 0);
    }

    private void Add(LinearLayout parent, View child, int top = 0, bool share = false, int start = 0)
    {
        var width = parent.Orientation == Orientation.Vertical ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent;
        var layout = new LinearLayout.LayoutParams(share ? 0 : width, ViewGroup.LayoutParams.WrapContent, share ? 1 : 0)
        { TopMargin = Dp(top), MarginStart = Dp(start) };
        parent.AddView(child, layout);
    }

    private void SetButtons(bool enabled)
    {
        foreach (var button in _buttons) button.Enabled = enabled;
    }

    private void Message(string value)
    {
        _notice.Text = value;
        _notice.Visibility = value.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private GradientDrawable Rounded(string color, int radius)
    {
        var shape = new GradientDrawable();
        shape.SetColor(Color.ParseColor(color));
        shape.SetCornerRadius(Dp(radius));
        return shape;
    }

    private static ColorStateList Colors(string value) => ColorStateList.ValueOf(Color.ParseColor(value))!;
    private int Dp(int amount) => (int)(amount * Resources!.DisplayMetrics!.Density + 0.5f);
}
