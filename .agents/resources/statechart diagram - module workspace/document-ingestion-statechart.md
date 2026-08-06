# WarpTalk - Workspace Document Ingestion Statechart Diagram Specification

This document provides the detailed state transition specifications for the Workspace Document Ingestion and RAG processing lifecycle, as shown in [document-ingestion-statechart.puml](file:///c:/Users/Admin/Documents/WarpTalk - Capstone Project/.agents/resources/statechart diagram - module workspace/document-ingestion-statechart.puml).

## 1. State Descriptions

| State | Sub-state / Phase | Description |
| :--- | :--- | :--- |
| **Pending** | `AwaitingProcessing` | Initial state when a document is uploaded. The metadata is saved, and it is awaiting AI eligibility verification. |
| **Skipped** | *None* | End state. Reached when AI features are disabled (`IsAiAllowed == false`). The document remains in the workspace but is not indexed. |
| **Processing** | `TextExtraction` | Active state where the C# Worker reads, decrypts, and extracts raw text from the document. |
| | `EmbeddingGeneration` | Active state where text chunks are processed, OpenAI embeddings are generated, and vectors are saved to Qdrant. |
| **Completed** | *None* | Successful end state. Document ingestion status is marked as `completed` and `ai_eligible = true` for RAG. |
| **Failed** | *None* | Error end state. Reached due to system exceptions or content filtering (safety violations). |

---

## 2. State Transition Table

| Current State | Event / Condition | Action | Next State |
| :--- | :--- | :--- | :--- |
| **[*] (Start)** | Document Uploaded | Initialize metadata (status = `pending`) | **Pending (AwaitingProcessing)** |
| **Pending** | `IsAiAllowed == true` | Update status to `processing` | **Processing (TextExtraction)** |
| **Pending** | `IsAiAllowed == false` | Update status to `skipped`, `ai_eligible = false` | **Skipped** |
| **Processing (TextExtraction)** | Raw text successfully extracted and cached | Publish `EmbeddingIndexRequest` message | **Processing (EmbeddingGeneration)** |
| **Processing (TextExtraction)** | File read error or text extraction exception | Update status to `failed`, `ai_eligible = false` | **Failed** |
| **Processing (EmbeddingGeneration)**| OpenAI API Success (Safe Content) | Save vectors to Qdrant, Publish `indexed` | **Completed** |
| **Processing (EmbeddingGeneration)**| OpenAI Content Filter violation | Publish `blocked` (reason = `content_filter`) | **Failed** |
| **Processing (EmbeddingGeneration)**| Redis queue error or OpenAI API Timeout | Update status to `failed`, `ai_eligible = false` | **Failed** |
| **Skipped / Completed / Failed** | Final cleanup | End document lifecycle | **[*] (End)** |
