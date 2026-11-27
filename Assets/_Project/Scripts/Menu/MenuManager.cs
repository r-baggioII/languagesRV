using System;
using System.Collections.Generic;
using UnityEngine;

// Supplies the current menu to the UI and to Gemini's instructions. It does
// not track or validate a conversational order.
[DisallowMultipleComponent]
public sealed class MenuManager : MonoBehaviour
{
    private static readonly IReadOnlyList<CafeMenuItem> NoItems =
        Array.Empty<CafeMenuItem>();
    private static readonly IReadOnlyList<CafeMilkCustomization> NoMilkOptions =
        Array.Empty<CafeMilkCustomization>();
    private static readonly IReadOnlyList<CafeMenuCustomization> NoExtras =
        Array.Empty<CafeMenuCustomization>();

    [SerializeField] private CafeMenuData menuData;

    public bool IsConfigured => menuData != null;

    public IReadOnlyList<CafeMenuItem> GetAllItems()
    {
        return menuData != null && menuData.Items != null
            ? menuData.Items
            : NoItems;
    }

    public CafeSizePricing GetSizePricing()
    {
        return menuData != null ? menuData.SizePricing : null;
    }

    public int GetPriceForSize(CafeMenuItem item, CafeDrinkSize size)
    {
        if (menuData == null)
        {
            throw new InvalidOperationException("MenuManager has no CafeMenuData reference assigned.");
        }

        // Formatter only passes products from this menu.
        return menuData.GetPriceForSize(item, size);
    }

    public IReadOnlyList<CafeMilkCustomization> GetMilkOptions()
    {
        return menuData != null && menuData.MilkOptions != null
            ? menuData.MilkOptions
            : NoMilkOptions;
    }

    public IReadOnlyList<CafeMenuCustomization> GetExtras()
    {
        return menuData != null && menuData.Extras != null
            ? menuData.Extras
            : NoExtras;
    }

    public static string FormatPrice(int cents)
    {
        return CafeMenuData.FormatPrice(cents);
    }

    public string BuildLlmMenuContext()
    {
        return CafeMenuLlmFormatter.BuildContext(this);
    }
}
