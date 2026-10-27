//
//  PersistenceController+Queries.swift
//  hyperwhisper
//
//  Transcript fetches, search and usage counters.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    // MARK: - Fetch Requests
    
    /// Fetches all transcripts sorted by date
    /// - Returns: Array of transcripts
    func fetchAllTranscripts() -> [Transcript] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to fetch transcripts: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch transcripts", tags: ["component": "PersistenceController", "operation": "fetchAllTranscripts"])
            return []
        }
    }
    
    /// Searches transcripts by text
    /// - Parameter searchText: Text to search for
    /// - Returns: Array of matching transcripts
    func searchTranscripts(containing searchText: String) -> [Transcript] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(format: "text CONTAINS[cd] %@", searchText)
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: false)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to search transcripts: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to search transcripts", tags: ["component": "PersistenceController", "operation": "searchTranscripts"])
            return []
        }
    }
    
    // MARK: - Usage Tracking Operations
    
    /// Gets or creates the usage tracking record
    /// There should only be one UsageTracking record in the database
    /// - Returns: The usage tracking record
    func getOrCreateUsageTracking() -> UsageTracking {
        let request: NSFetchRequest<UsageTracking> = UsageTracking.fetchRequest()
        request.fetchLimit = 1
        
        do {
            let results = try container.viewContext.fetch(request)
            if let existing = results.first {
                return existing
            }
        } catch {
            AppLogger.coreData.error("Failed to fetch usage tracking: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch usage tracking", tags: ["component": "PersistenceController", "operation": "getOrCreateUsageTracking"])
        }
        
        // Create new usage tracking record
        let usage = UsageTracking(context: container.viewContext)
        usage.id = UUID()
        usage.dailyTranscriptionSeconds = 0
        usage.totalModelsDownloaded = 0
        usage.lastResetDate = Date()
        usage.firstUsageDate = Date()
        usage.licenseStatus = "trial"
        
        save()
        return usage
    }
    
    // NOTE: usage WRITES (updateDailyUsage / updateModelDownloadCount /
    // resetDailyUsage) were removed — the Rust hw-license core owns usage
    // tracking now. The getters below remain solely for the one-shot
    // Core Data → UserDefaults seed in RustLicenseStore.

    /// Gets current daily usage in seconds
    /// - Returns: Number of seconds used today
    func getDailyUsage() -> Int64 {
        let usage = getOrCreateUsageTracking()
        
        // Check if we need to reset (new day)
        if let lastReset = usage.lastResetDate {
            let calendar = Calendar.current
            if !calendar.isDateInToday(lastReset) {
                // Reset for new day
                usage.dailyTranscriptionSeconds = 0
                usage.lastResetDate = Date()
                save()
                return 0
            }
        }
        
        return usage.dailyTranscriptionSeconds
    }
    
    /// Gets the count of downloaded models
    /// - Returns: Number of models downloaded
    func getModelDownloadCount() -> Int16 {
        let usage = getOrCreateUsageTracking()
        return usage.totalModelsDownloaded
    }
}
