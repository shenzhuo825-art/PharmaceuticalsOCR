using System.Reflection;
using PrescriptionScanner;

// Exercise parsing without loading OCR models or reading patient images.
var parse = typeof(PrescriptionModel).Assembly.GetType("PrescriptionScanner.Program")!
    .GetMethod("FindMedications", BindingFlags.NonPublic | BindingFlags.Static)!;
List<MedicationModel> Read(params string[] lines) =>
    (List<MedicationModel>)parse.Invoke(null, new object[] { lines })!;

foreach (var amount in new[] { "1.00", "0.50", "1.25", "1．00" })
{
    var rows = Read("TEST MEDICINE", amount + " 錠 x2x28 56.0");
    if (rows.Count != 1 || rows[0].Dose != amount.Replace('．', '.') + " 錠" ||
        rows[0].Frequency != "每日2次" || rows[0].Days != 28 || rows[0].TotalQuantity != 56m)
        throw new Exception("Decimal dose regression: " + amount);
}

foreach (var unit in new[] { "錠", "锭" })
{
    var rows = Read($"1 TEST MEDICINE A 内服 1.00 {unit} x2x28 56.0",
        $"2 TEST MEDICINE B 内服 1.00 {unit} x1x28 28.0");
    if (rows.Count != 2 || rows[0].Name != "1 TEST MEDICINE A" ||
        rows[1].Name != "2 TEST MEDICINE B" || rows[0].Dose != "1.00 錠" ||
        rows[0].TotalQuantity != 56m || rows[1].TotalQuantity != 28m)
        throw new Exception("Inline medication row regression: " + unit);
}
Console.WriteLine("6 medication parser regression scenarios passed.");
