using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Selection filter that allows picking only elements from specific categories
/// that contain a non-empty system parameter "Comments"
/// and optionally match a required value.
/// 
/// Default allowed categories (if none specified):
/// - OST_MechanicalEquipment, OST_ElectricalEquipment, OST_DataDevices, OST_FireAlarmDevices
/// 
/// Custom categories can be specified via constructor to restrict selection to specific categories.
/// </summary>
public class CommentsFilter : ISelectionFilter
{
    private readonly string _requiredValue;
    private readonly BuiltInCategory[] _allowedCategories;

    /// <summary>
    /// Creates a filter that accepts any element with non-empty Comments
    /// from the default set of allowed categories.
    /// Default categories: MechanicalEquipment, ElectricalEquipment, DataDevices, FireAlarmDevices.
    /// </summary>
    public CommentsFilter()
    {
        _requiredValue = null;
        _allowedCategories = null;
    }

    /// <summary>
    /// Creates a filter that accepts only elements whose Comments parameter
    /// equals the specified value, from the default set of allowed categories.
    /// Default categories: MechanicalEquipment, ElectricalEquipment, DataDevices, FireAlarmDevices.
    /// </summary>
    public CommentsFilter(string requiredValue)
    {
        _requiredValue = requiredValue;
        _allowedCategories = null;
    }

    /// <summary>
    /// Creates a filter that accepts only elements whose Comments parameter
    /// equals the specified value, and from the specified categories.
    /// 
    /// If allowedCategories is null or empty, uses default categories:
    /// MechanicalEquipment, ElectricalEquipment, DataDevices, FireAlarmDevices.
    /// </summary>
    /// <param name="requiredValue">Required value for Comments parameter (can be null for any value).</param>
    /// <param name="allowedCategories">Specific categories to allow (null or empty = use defaults).</param>
    public CommentsFilter(string requiredValue, params BuiltInCategory[] allowedCategories)
    {
        _requiredValue = requiredValue;
        _allowedCategories = (allowedCategories?.Length ?? 0) > 0 ? allowedCategories : null;
    }

    public bool AllowElement(Element elem)
    {
        if (elem == null)
            return false;

        // ── Determine allowed categories ──
        var allowedCats = GetAllowedCategories();

        // ── Check category ──
        int? catId = elem.Category?.Id?.IntegerValue;
        if (catId == null || !allowedCats.Contains((BuiltInCategory)catId.Value))
            return false;

        // ── Read system parameter "Comments" ──
        string comments = elem
            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            ?.AsString();

        // ── Must be non-empty ──
        if (string.IsNullOrWhiteSpace(comments))
            return false;

        // ── If filter requires specific value — check it ──
        if (_requiredValue != null &&
            !string.Equals(comments.ToUpper(), _requiredValue, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
        return true;
    }

    /// <summary>
    /// Returns the list of allowed categories.
    /// If custom categories were specified in constructor, returns them.
    /// Otherwise returns the default set of 4 categories.
    /// </summary>
    private List<BuiltInCategory> GetAllowedCategories()
    {
        if (_allowedCategories != null && _allowedCategories.Length > 0)
            return new List<BuiltInCategory>(_allowedCategories);

        // Default categories
        return new List<BuiltInCategory>
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_FireAlarmDevices
        };
    }
}
