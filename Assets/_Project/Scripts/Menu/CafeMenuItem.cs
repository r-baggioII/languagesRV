using System;
using UnityEngine;

public enum CafeMenuCategory
{
    Coffee,
    ColdDrinks,
    TeaAndOther,
    Bakery,
    Breakfast
}

public enum CafeDrinkSize
{
    Small,
    Medium,
    Large
}

public enum CafeTemperature
{
    Hot,
    Iced
}

[Serializable]
public sealed class CafeMenuItem
{
    [SerializeField] private string displayName;
    [SerializeField] private CafeMenuCategory category;
    [SerializeField, TextArea] private string description;
    [SerializeField, Min(0)] private int basePriceCents;
    [SerializeField] private bool hasSizes;
    [SerializeField] private bool supportsHot;
    [SerializeField] private bool supportsIced;
    [SerializeField] private bool supportsMilkCustomization;

    public string DisplayName => displayName;
    public CafeMenuCategory Category => category;
    public string Description => description;
    public int BasePriceCents => basePriceCents;
    public bool HasSizes => hasSizes;
    public bool SupportsHot => supportsHot;
    public bool SupportsIced => supportsIced;
    public bool SupportsMilkCustomization => supportsMilkCustomization;

    public CafeMenuItem(
        string displayName,
        CafeMenuCategory category,
        int basePriceCents,
        bool hasSizes,
        bool supportsHot,
        bool supportsIced,
        bool supportsMilkCustomization,
        string description = "")
    {
        this.displayName = displayName;
        this.category = category;
        this.description = description;
        this.basePriceCents = basePriceCents;
        this.hasSizes = hasSizes;
        this.supportsHot = supportsHot;
        this.supportsIced = supportsIced;
        this.supportsMilkCustomization = supportsMilkCustomization;
    }
}
