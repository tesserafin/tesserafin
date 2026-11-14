using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Tesserafin.Data.Enums;

namespace Tesserafin.Data.Entities
{
    /// <summary>
    /// An <see cref="ItemDisplayPreferences"/> row removed from the active table because another row of
    /// the same scope was kept. Every column of the original row is preserved, so that the row can be
    /// recovered; nothing in the server reads this table.
    /// </summary>
    public class ItemDisplayPreferencesArchive
    {
        /// <summary>
        /// Gets or sets the id the row had in <see cref="ItemDisplayPreferences"/>.
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the id of the associated item.
        /// </summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the client string.
        /// </summary>
        [MaxLength(32)]
        [StringLength(32)]
        public required string Client { get; set; }

        /// <summary>
        /// Gets or sets the view type.
        /// </summary>
        public ViewType ViewType { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the indexing should be remembered.
        /// </summary>
        public bool RememberIndexing { get; set; }

        /// <summary>
        /// Gets or sets what the view should be indexed by.
        /// </summary>
        public IndexingKind? IndexBy { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the sorting type should be remembered.
        /// </summary>
        public bool RememberSorting { get; set; }

        /// <summary>
        /// Gets or sets what the view should be sorted by.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public required string SortBy { get; set; }

        /// <summary>
        /// Gets or sets the sort order.
        /// </summary>
        public SortOrder SortOrder { get; set; }

        /// <summary>
        /// Gets or sets the id of the row of the same scope that stayed active, as it was when this row
        /// was archived.
        /// </summary>
        public int KeptId { get; set; }

        /// <summary>
        /// Gets or sets the kind of duplicate group the row belonged to.
        /// </summary>
        public ItemDisplayPreferencesDuplicateClass DuplicateClass { get; set; }

        /// <summary>
        /// Gets or sets when the row was archived, in UTC.
        /// </summary>
        public DateTime ArchivedAt { get; set; }
    }
}
