using System;

namespace ARBM.CableJournalExporter.Models
{
    public enum CableInstallation { Concealed, Tray }

    // Доли фактического рассчитанного маршрута; единицы одинаковы для обеих граф.
    // Итоговая длина с коэффициентом, округлением и запасом распределяется при экспорте.
    public sealed class CableInstallationLengths
    {
        public double Concealed { get; }
        public double Tray { get; }
        public CableInstallationLengths(double concealed, double tray)
        {
            if (double.IsNaN(concealed) || double.IsInfinity(concealed) || concealed < 0 ||
                double.IsNaN(tray) || double.IsInfinity(tray) || tray < 0)
                throw new ArgumentException("Некорректные длины способов прокладки.");
            Concealed = concealed;
            Tray = tray;
        }

        public static CableInstallation FromTypeName(string name)
        {
            // Revit возвращает либо имя типа, либо «семейство : тип».
            string value = (name ?? "").Trim();
            int colon = value.LastIndexOf(':');
            if (colon >= 0) value = value.Substring(colon + 1).Trim();
            switch (value)
            {
                case "В лотке": return CableInstallation.Tray;
                case "": // Участки без параметра и служебные соединения — скрыто по умолчанию.
                case "Без засечки":
                case "В ПВХ-канале":
                case "В гибкой трубе":
                case "В жёсткой трубе":
                case "В жесткой трубе": return CableInstallation.Concealed;
                default: throw new ArgumentException("Неизвестный способ прокладки кабеля: «" + value + "».");
            }
        }

        public CableInstallationLengths Allocate(double total)
        {
            if (double.IsNaN(total) || double.IsInfinity(total) || total < 0)
                throw new ArgumentException("Некорректная итоговая длина кабеля.");
            double sum = Concealed + Tray;
            if (sum == 0 || Tray == 0) return new CableInstallationLengths(total, 0);
            if (Concealed == 0) return new CableInstallationLengths(0, total);
            int digits = total == Math.Truncate(total) ? 0 : 2;
            double tray = Math.Min(total, Math.Round(total * (Tray / sum), digits, MidpointRounding.AwayFromZero));
            return new CableInstallationLengths(total - tray, tray);
        }
    }
}
