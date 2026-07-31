using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;

namespace UiaTrigger.Inspection;

/// <summary>要素のプロパティ読み取り (UiaDispatcher スレッド上で使用)。</summary>
internal static class PropertyReader
{
    /// <summary>キャッシュ済みパターンを取り出す。非対応・未キャッシュなら null。</summary>
    private static T? GetCachedPattern<T>(IUIAutomationElement element, int patternId) where T : class
    {
        try
        {
            element.GetCachedPattern(patternId, out nint pointer);
            return UiaFactory.WrapUnique<T>(pointer);
        }
        catch (COMException)
        {
            // CacheRequest に入れ忘れた / プロバイダーが返さない
            return null;
        }
    }

    /// <summary>UIA プロパティ変更イベント購読に使う PropertyId。</summary>
    public static int ToPropertyId(PropertyClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return clause.Property switch
        {
            TriggerProperty.Name => UiaIds.NameProperty,
            TriggerProperty.AutomationId => UiaIds.AutomationIdProperty,
            TriggerProperty.ClassName => UiaIds.ClassNameProperty,
            TriggerProperty.ControlType => UiaIds.ControlTypeProperty,
            TriggerProperty.BoundingRectangle => UiaIds.BoundingRectangleProperty,
            TriggerProperty.AccessKey => UiaIds.AccessKeyProperty,
            TriggerProperty.AcceleratorKey => UiaIds.AcceleratorKeyProperty,
            TriggerProperty.HelpText => UiaIds.HelpTextProperty,
            TriggerProperty.IsEnabled => UiaIds.IsEnabledProperty,
            TriggerProperty.IsOffscreen => UiaIds.IsOffscreenProperty,
            TriggerProperty.Value => UiaIds.ValueValueProperty,
            TriggerProperty.RangeValue => UiaIds.RangeValueValueProperty,
            TriggerProperty.RangeValueMinimum => UiaIds.RangeValueMinimumProperty,
            TriggerProperty.RangeValueMaximum => UiaIds.RangeValueMaximumProperty,
            TriggerProperty.Custom => clause.CustomPropertyId,
            _ => throw new ArgumentOutOfRangeException(nameof(clause)),
        };
    }

    /// <summary>
    /// 列挙にない UIA プロパティを読む (<see cref="TriggerProperty.Custom"/> の逃げ道)。
    ///
    /// 名前付きプロパティと違って CacheRequest に載っていないため、句 1 個につき
    /// 1 往復かかる。逃げ道であることを承知で使うもの。
    /// </summary>
    public static ClauseValue ReadCustom(IUIAutomationElement element, int propertyId)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (propertyId == 0)
        {
            return ClauseValue.Unsupported;
        }
        ComVariant variant;
        try
        {
            // Ex 版で ignoreDefaultValue=true。既定値 (プロバイダーが未実装のとき UIA が
            // 代わりに返す値) を「本物の値」として扱わないため
            element.GetCurrentPropertyValueEx(propertyId, ignoreDefaultValue: true, out variant);
        }
        catch (COMException)
        {
            return ClauseValue.Unsupported;
        }

        using (variant)
        {
            return FromVariant(variant);
        }
    }

    /// <summary>VARIANT を評価器の値に変換する。扱えない型は「非対応」とする。</summary>
    private static ClauseValue FromVariant(ComVariant variant) => variant.VarType switch
    {
        VarEnum.VT_BSTR => ClauseValue.FromText(ComparisonString.FromText(variant.As<string?>())),
        VarEnum.VT_BOOL => ClauseValue.FromBoolean(variant.As<bool>()),
        VarEnum.VT_I2 => ClauseValue.FromNumber(variant.As<short>()),
        VarEnum.VT_I4 => ClauseValue.FromNumber(variant.As<int>()),
        VarEnum.VT_I8 => ClauseValue.FromNumber(variant.As<long>()),
        VarEnum.VT_R4 => ClauseValue.FromNumber(variant.As<float>()),
        VarEnum.VT_R8 => ClauseValue.FromNumber(variant.As<double>()),
        _ => ClauseValue.Unsupported,
    };

    /// <summary>
    /// 全監視対象プロパティのスナップショットを取得する。
    ///
    /// 個別の get_Current* で読むと **16 往復**になり、それがプロパティ変化イベントの
    /// たびに走ることになる。BuildUpdatedCache + get_Cached* で **1 往復**にまとめる
    /// (docs/DESIGN.md B1)。
    /// パスワード欄の値はここで落とす (C12)。
    /// </summary>
    public static ElementPropertySnapshot ReadSnapshot(UiaContext context, IUIAutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);

        element.BuildUpdatedCache(context.SnapshotCacheRequest, out nint pointer);
        var cached = UiaFactory.WrapUniqueRequired<IUIAutomationElement>(pointer);
        try
        {
            return ReadCachedSnapshot(cached).RedactIfPassword();
        }
        finally
        {
            UiaFactory.ReleaseUnique(cached);
        }
    }

    private static ElementPropertySnapshot ReadCachedSnapshot(IUIAutomationElement element)
    {
        element.get_CachedName(out string name);
        element.get_CachedAutomationId(out string automationId);
        element.get_CachedClassName(out string className);
        element.get_CachedControlType(out int controlType);
        element.get_CachedLocalizedControlType(out string localizedControlType);
        element.get_CachedBoundingRectangle(out UiaRect rect);
        element.get_CachedAccessKey(out string accessKey);
        element.get_CachedAcceleratorKey(out string acceleratorKey);
        element.get_CachedHelpText(out string helpText);
        element.get_CachedIsEnabled(out bool isEnabled);
        element.get_CachedIsOffscreen(out bool isOffscreen);
        element.get_CachedIsPassword(out bool isPassword);

        string? value = null;
        bool supportsValue = false;
        var valuePattern = GetCachedPattern<IUIAutomationValuePattern>(element, UiaIds.ValuePattern);
        if (valuePattern is not null)
        {
            try
            {
                valuePattern.get_CachedValue(out string v);
                value = v;
                supportsValue = true;
            }
            catch (COMException)
            {
                // パターンはあるが値がキャッシュされていない (プロバイダー依存)。非対応として扱う
            }
            finally
            {
                UiaFactory.ReleaseUnique(valuePattern);
            }
        }

        double? rangeValue = null, rangeMin = null, rangeMax = null;
        bool supportsRange = false;
        var rangePattern = GetCachedPattern<IUIAutomationRangeValuePattern>(element, UiaIds.RangeValuePattern);
        if (rangePattern is not null)
        {
            try
            {
                rangePattern.get_CachedValue(out double d);
                rangePattern.get_CachedMinimum(out double min);
                rangePattern.get_CachedMaximum(out double max);
                rangeValue = d;
                rangeMin = min;
                rangeMax = max;
                supportsRange = true;
            }
            catch (COMException)
            {
            }
            finally
            {
                UiaFactory.ReleaseUnique(rangePattern);
            }
        }

        return new ElementPropertySnapshot
        {
            Name = name,
            AutomationId = automationId,
            ClassName = className,
            ControlType = controlType,
            // 識別用 (安定・英語固定) と表示用 (プロバイダーのロケール) を分けて持つ。
            // 混ぜると「表示を直したら条件評価が壊れる」に戻る (docs/DESIGN.md L6 / L4)
            ControlTypeName = UiaControlTypeNames.GetName(controlType),
            LocalizedControlType = string.IsNullOrEmpty(localizedControlType)
                ? UiaControlTypeNames.GetName(controlType)
                : localizedControlType,
            BoundingRectangle = new ElementRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            AccessKey = accessKey,
            AcceleratorKey = acceleratorKey,
            HelpText = helpText,
            IsEnabled = isEnabled,
            IsOffscreen = isOffscreen,
            IsPassword = isPassword,
            SupportsValuePattern = supportsValue,
            Value = value,
            SupportsRangeValuePattern = supportsRange,
            RangeValue = rangeValue,
            RangeValueMinimum = rangeMin,
            RangeValueMaximum = rangeMax,
        };
    }
}
