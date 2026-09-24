namespace PD.PcbTools;

/// <summary>
/// Thin versioned PD facade over Engine physical-symbol operations and
/// capability groups. Labels resolve by Engine numeric identity, never by
/// enum member name: an Engine rename keeps its value and flows through
/// without PD edits, while unknown future identities render an explicit
/// fallback instead of throwing. PD owns page presentation; Engine owns
/// the operation and group contracts. See PhysicalSymbolOwnership.md.
/// </summary>
public static class PhysicalSymbolOperationLabels
{
    public const int FacadeVersion = 1;

    public static string TitleForOperationId(int operationId) => operationId switch
    {
        0 => "Generate symbol",
        1 => "Define padstack",
        2 => "Replace pin padstack",
        3 => "Place pin array",
        4 => "Place pin",
        5 => "Align pins",
        6 => "Mark pin one",
        7 => "Renumber pins",
        8 => "Draw assembly outline",
        9 => "Draw place bound",
        10 => "Set height",
        11 => "Place refdes",
        12 => "Place fiducial",
        13 => "Add keepout",
        14 => "Add hole or slot",
        15 => "Convert shape to pad",
        16 => "Standardize BGA",
        _ => $"Symbol operation {operationId}",
    };

    public static string GroupLabelForGroupId(int groupId) => groupId switch
    {
        0 => "Generation",
        1 => "Pins",
        2 => "Outlines and bounds",
        3 => "Holes",
        4 => "Keepouts",
        5 => "Pad replacement",
        6 => "Padstack definitions",
        _ => $"Group {groupId}",
    };
}
