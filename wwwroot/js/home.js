const connection = new signalR.HubConnectionBuilder()
    .withUrl("/workhub")
    .withAutomaticReconnect()
    .build();

// Add form submission handler to prevent default redirect
document.getElementById('uploadForm').addEventListener('submit', function (e) {
    e.preventDefault();

    const formData = new FormData(this);
    const uploadStatus = document.getElementById('upload-status');
    uploadStatus.innerHTML = '<div class="alert alert-info">Uploading files...</div>';

    fetch('@Url.Action("UploadFile", "Home")', {
        method: 'POST',
        body: formData
    })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                uploadStatus.innerHTML = `<div class="alert alert-success">${data.message}</div>`;
                document.getElementById('uploadForm').elements.files.value = null;
            } else {
                uploadStatus.innerHTML = `<div class="alert alert-danger">${data.message}</div>`;
            }
        })
        .catch(error => {
            uploadStatus.innerHTML = `<div class="alert alert-danger">Upload failed: ${error.message}</div>`;
        });
});

// Track complete responses for each chunk using position as identifier
let responseHistory = new Map();
let activeChunkIds = new Set();

// Map to track DOM elements for each work item
const workItemElements = new Map();

connection.on("UpdateStatus", (processed, remaining, currentWork, tokensPerSecond, totalTokens) => {
    // Update status counters directly
    document.getElementById("processed").textContent = processed;
    document.getElementById("remaining").textContent = remaining;
    document.getElementById("tokensGenerated").textContent = totalTokens || "0";
    document.getElementById("tokensPerSecond").textContent =
        tokensPerSecond ? tokensPerSecond.toFixed(1) : "-";

    const workDiv = document.getElementById("currentWork");

    // Get current chunk identifiers
    const currentChunkIds = new Set();
    currentWork.forEach(w => {
        if (w.id && w.id.startPosition !== undefined && w.id.endPosition !== undefined) {
            currentChunkIds.add(`${w.id.fileId || ''}-${w.id.startPosition}-${w.id.endPosition}`);
        }
    });

    // Check if we have a new batch
    const newBatchDetected = !setsEqual(activeChunkIds, currentChunkIds);
    if (newBatchDetected) {
        // Clear previous elements and history
        workDiv.innerHTML = '';
        workItemElements.clear();
        responseHistory.clear();
        activeChunkIds = currentChunkIds;
    }

    // Process each work item
    currentWork.forEach(w => {
        const key = w.id && w.id.startPosition !== undefined ?
            `${w.id.fileId || ''}-${w.id.startPosition}-${w.id.endPosition}` :
            JSON.stringify(w.id);

        const newToken = w.response ?? '';

        // Get or create work item element - to optimize performance by making the minimum possible DOM manipulations
        let workItemEl = workItemElements.get(key);
        if (!workItemEl) {
            // Create new work item elements
            workItemEl = document.createElement('div');
            workItemEl.className = 'work-item';

            const statusEl = document.createElement('div');
            statusEl.className = 'status-line';
            statusEl.addEventListener('click', function () { // Make it toggleable
                workItemEl.classList.toggle('collapsed');
            });
            workItemEl.appendChild(statusEl);

            const responseLabel = document.createElement('div');
            responseLabel.textContent = 'Response:';
            responseLabel.className = 'response-label';
            workItemEl.appendChild(responseLabel);

            const responseEl = document.createElement('pre');
            responseEl.className = 'response-content';
            workItemEl.appendChild(responseEl);

            workDiv.appendChild(workItemEl);
            workItemElements.set(key, workItemEl);
        }

        // Update status
        const statusEl = workItemEl.querySelector('.status-line');
        statusEl.textContent = `Status: ${w.status}`;

        // Only append new tokens if processing isn't complete
        if (!w.status.includes("Processing complete") && newToken) {
            // Get existing response or initialize
            const existingResponse = responseHistory.get(key) ?? '';
            const newResponse = existingResponse + newToken;
            responseHistory.set(key, newResponse);

            // Append just the new token to the pre element
            const responseEl = workItemEl.querySelector('.response-content');
            const textNode = document.createTextNode(newToken);
            responseEl.appendChild(textNode);
        }
    });
});

// Helper function to escape HTML
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Helper function to compare sets
function setsEqual(a, b) {
    if (a.size !== b.size) return false;
    for (const item of a) {
        if (!b.has(item)) return false;
    }
    return true;
}

connection.start();