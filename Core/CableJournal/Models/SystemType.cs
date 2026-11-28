
namespace ARBM.CableJournalExporter.Models
{
    /// <summary>
    /// Defines the list of supported engineering systems for which
    /// cable journals can be generated within the application.
    /// <para>RU: Определяет перечень инженерных систем, для которых
    /// может формироваться кабельный журнал в рамках приложения.</para>
    /// </summary>
    public enum SystemType
    {
        /// <summary>
        /// Automatic Fire Alarm System (APS).
        /// <para>RU: Автоматическая пожарная сигнализация (АПС).</para>
        /// </summary>
        Aps,
        /// <summary>
        /// Voice Alarm and Evacuation Control System (PA/VA).
        /// <para>RU: Система оповещения и управления эвакуацией (СОУЭ).</para>
        /// </summary>
        Souye,
        /// <summary>
        /// Security Alarm System.
        /// <para>RU: Система охранной сигнализации (СОС).</para>
        /// </summary>
        Security,

        /// <summary>
        /// Access Control System (ACS).
        /// <para>RU: Система контроля и управления доступом (СКУД).</para>
        /// </summary>
        Skud,

        /// <summary>
        /// Structured Cabling System (SCS).
        /// <para>RU: Структурированная кабельная система (СКС).</para>
        /// </summary>
        Sks
    }
}
