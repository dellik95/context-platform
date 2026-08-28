using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Embeddings",
                columns: table => new
                {
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Dim = table.Column<int>(type: "INTEGER", nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Embeddings", x => x.ContentHash);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    VcsRemote = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClaudeIntegrations",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HookVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    McpConfigHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    InstalledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    BackupPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaudeIntegrations", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ClaudeIntegrations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaudeSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderModels_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AstVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileVersions_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GraphNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RefId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OwningFileId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphNodes_Files_OwningFileId",
                        column: x => x.OwningFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GraphNodes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Symbols",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentSymbolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    QualifiedName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    StartLine = table.Column<int>(type: "INTEGER", nullable: false),
                    EndLine = table.Column<int>(type: "INTEGER", nullable: false),
                    SignatureHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BodyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DocComment = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Symbols", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Symbols_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Symbols_Symbols_ParentSymbolId",
                        column: x => x.ParentSymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.69999999999999996),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReinforcementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedFromSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastReinforcedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memories_Memories_SupersededById",
                        column: x => x.SupersededById,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memories_Sessions_CreatedFromSessionId",
                        column: x => x.CreatedFromSessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SessionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TokenEstimate = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionEvents_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSummaries",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Completed = table.Column<string>(type: "TEXT", nullable: true),
                    Discovered = table.Column<string>(type: "TEXT", nullable: true),
                    Decisions = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedFilesJson = table.Column<string>(type: "TEXT", nullable: true),
                    OpenIssues = table.Column<string>(type: "TEXT", nullable: true),
                    NextSteps = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSummaries", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_SessionSummaries_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GraphEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OwningFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Weight = table.Column<double>(type: "REAL", nullable: false, defaultValue: 1.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphEdges_Files_OwningFileId",
                        column: x => x.OwningFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_TargetNodeId",
                        column: x => x.TargetNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GraphEdges_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SymbolId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TokenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chunks_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Chunks_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SymbolRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSymbolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSymbolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsResolved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolRelations_Symbols_SourceSymbolId",
                        column: x => x.SourceSymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SymbolRelations_Symbols_TargetSymbolId",
                        column: x => x.TargetSymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemoryEmbeddings",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Dim = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEmbeddings", x => x.MemoryId);
                    table.ForeignKey(
                        name: "FK_MemoryEmbeddings_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryFiles",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryFiles", x => new { x.MemoryId, x.FileId });
                    table.ForeignKey(
                        name: "FK_MemoryFiles_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemoryFiles_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedMemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryRelations_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MemoryRelations_Memories_RelatedMemoryId",
                        column: x => x.RelatedMemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemorySymbols",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SymbolId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemorySymbols", x => new { x.MemoryId, x.SymbolId });
                    table.ForeignKey(
                        name: "FK_MemorySymbols_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemorySymbols_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_ContentHash",
                table: "Chunks",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_FileId",
                table: "Chunks",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_SymbolId",
                table: "Chunks",
                column: "SymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ContentHash",
                table: "Files",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ProjectId",
                table: "Files",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ProjectId_RelativePath",
                table: "Files",
                columns: new[] { "ProjectId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileVersions_FileId",
                table: "FileVersions",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_OwningFileId",
                table: "GraphEdges",
                column: "OwningFileId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_ProjectId",
                table: "GraphEdges",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_SourceNodeId_Kind",
                table: "GraphEdges",
                columns: new[] { "SourceNodeId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_TargetNodeId_Kind",
                table: "GraphEdges",
                columns: new[] { "TargetNodeId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_OwningFileId",
                table: "GraphNodes",
                column: "OwningFileId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_ProjectId",
                table: "GraphNodes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_RefId",
                table: "GraphNodes",
                column: "RefId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_NextRetryAt",
                table: "Jobs",
                column: "NextRetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Type_Status",
                table: "Jobs",
                columns: new[] { "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_CreatedFromSessionId",
                table: "Memories",
                column: "CreatedFromSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Memories_ProjectId_Type",
                table: "Memories",
                columns: new[] { "ProjectId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_SupersededById",
                table: "Memories",
                column: "SupersededById");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFiles_FileId",
                table: "MemoryFiles",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryRelations_MemoryId",
                table: "MemoryRelations",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryRelations_RelatedMemoryId",
                table: "MemoryRelations",
                column: "RelatedMemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemorySymbols_SymbolId",
                table: "MemorySymbols",
                column: "SymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_RootPath",
                table: "Projects",
                column: "RootPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderModels_ProviderId",
                table: "ProviderModels",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Category_Name",
                table: "Providers",
                columns: new[] { "Category", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_SessionId",
                table: "SessionEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_SessionId_Type",
                table: "SessionEvents",
                columns: new[] { "SessionId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ClaudeSessionId",
                table: "Sessions",
                column: "ClaudeSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ProjectId",
                table: "Sessions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SymbolRelations_SourceSymbolId",
                table: "SymbolRelations",
                column: "SourceSymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_SymbolRelations_SourceSymbolId_Kind",
                table: "SymbolRelations",
                columns: new[] { "SourceSymbolId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolRelations_TargetSymbolId",
                table: "SymbolRelations",
                column: "TargetSymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_FileId",
                table: "Symbols",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_ParentSymbolId",
                table: "Symbols",
                column: "ParentSymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_QualifiedName",
                table: "Symbols",
                column: "QualifiedName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Chunks");

            migrationBuilder.DropTable(
                name: "ClaudeIntegrations");

            migrationBuilder.DropTable(
                name: "Embeddings");

            migrationBuilder.DropTable(
                name: "FileVersions");

            migrationBuilder.DropTable(
                name: "GraphEdges");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "MemoryEmbeddings");

            migrationBuilder.DropTable(
                name: "MemoryFiles");

            migrationBuilder.DropTable(
                name: "MemoryRelations");

            migrationBuilder.DropTable(
                name: "MemorySymbols");

            migrationBuilder.DropTable(
                name: "ProviderModels");

            migrationBuilder.DropTable(
                name: "SessionEvents");

            migrationBuilder.DropTable(
                name: "SessionSummaries");

            migrationBuilder.DropTable(
                name: "SymbolRelations");

            migrationBuilder.DropTable(
                name: "GraphNodes");

            migrationBuilder.DropTable(
                name: "Memories");

            migrationBuilder.DropTable(
                name: "Providers");

            migrationBuilder.DropTable(
                name: "Symbols");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
