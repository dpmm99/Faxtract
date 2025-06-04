const connection = new signalR.HubConnectionBuilder()
    .withUrl("/workhub")
    .withAutomaticReconnect()
    .build();

// Add form submission handler to prevent default redirect
document.getElementById('uploadForm').addEventListener('submit', function (e) {
    e.preventDefault();

    const formData = new FormData(this);
    const stripHtml = document.getElementById('stripHtml').checked;
    formData.set('stripHtml', stripHtml);

    const uploadStatus = document.getElementById('upload-status');
    uploadStatus.innerHTML = '<div class="alert alert-info">Uploading files...</div>';

    fetch('Home/UploadFile', {
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

function loadFlashCardChart() {
    fetch('Home/GetFlashCardChartData')
        .then(response => response.json())
        .then(data => {
            renderFlashCardChart(data);
        })
        .catch(error => {
            console.error('Error loading chart data:', error);
            document.getElementById('flashCardChart').innerHTML =
                `<div class="alert alert-danger">Failed to load chart data: ${error.message}</div>`;
        });
}

function renderFlashCardChart(chartData) {
    const chartContainer = document.getElementById('flashCardChart');
    chartContainer.innerHTML = ''; // Clear previous chart

    if (chartData.length === 0) {
        chartContainer.innerHTML = '<div class="alert alert-info">No flash card data available</div>';
        return;
    }

    // Group data by FileId for better organization
    const fileGroups = {};
    chartData.forEach(item => {
        if (!fileGroups[item.fileId]) {
            fileGroups[item.fileId] = [];
        }
        fileGroups[item.fileId].push(item);
    });

    // Find the maximum flash card count for color scaling
    const maxCount = Math.max(...chartData.map(item => item.flashCardCount));

    // Create chart with file sections
    Object.keys(fileGroups).forEach(fileId => {
        const fileData = fileGroups[fileId];

        // Create file section
        const fileSection = document.createElement('div');
        fileSection.className = 'file-section';

        // Add file header
        const fileHeader = document.createElement('h4');
        fileHeader.textContent = fileId;
        fileHeader.className = 'file-header';
        fileSection.appendChild(fileHeader);

        // Create chart grid for this file
        const chartGrid = document.createElement('div');
        chartGrid.className = 'chart-grid';

        // Add boxes for each chunk
        fileData.forEach(chunk => {
            const box = document.createElement('div');
            box.className = 'chart-box';

            // Calculate color intensity based on flash card count (0-5 scale)
            const intensity = maxCount > 0 ? Math.min(5, Math.ceil((chunk.flashCardCount / maxCount) * 5)) : 0;
            box.classList.add(`intensity-${intensity}`);

            // Add tooltip with details
            box.title = `Chunk ID: ${chunk.chunkId}\nFlash Cards: ${chunk.flashCardCount}`;

            // Add click event to show the modal with flash card details
            box.addEventListener('click', () => showFlashCardDetails(chunk.chunkId));

            chartGrid.appendChild(box);
        });

        fileSection.appendChild(chartGrid);
        chartContainer.appendChild(fileSection);
    });
}

function showFlashCardDetails(chunkId) {
    // Fetch flash card details from the server
    fetch(`Home/GetFlashCardDetails?chunkId=${chunkId}`)
        .then(response => response.json())
        .then(data => {
            // Create or get modal container
            let modal = document.getElementById('flashCardModal');
            if (!modal) {
                modal = document.createElement('div');
                modal.id = 'flashCardModal';
                document.body.appendChild(modal);
            }

            // Populate modal content
            modal.innerHTML = `
                <div class="modal-content">
                    <div class="modal-header">
                        <h3>Flash Card Details</h3>
                        <button class="modal-close" onclick="document.getElementById('flashCardModal').style.display = 'none'">&times;</button>
                    </div>
                    ${renderChunkInfo(data.chunk)}
                    <div class="flash-cards-container">
                        ${renderFlashCards(data.flashCards)}
                    </div>
                </div>
            `;

            // Show the modal
            modal.style.display = 'block';

            // Add event to close modal when clicking outside
            modal.addEventListener('click', function (event) {
                if (event.target === modal) {
                    modal.style.display = 'none';
                }
            });
        })
        .catch(error => {
            console.error('Error loading flash card details:', error);
            alert('Failed to load flash card details. Please try again.');
        });
}

// Helper function to render chunk info
function renderChunkInfo(chunk) {
    if (!chunk) {
        return '<div class="chunk-info">No chunk data available</div>';
    }

    return `
        <div class="chunk-info">
            <div class="chunk-actions">
                <button class="btn btn-danger btn-sm" onclick="deleteAllFlashCards(${chunk.id})">Delete Chunk</button>
                <button class="btn btn-primary btn-sm" onclick="retryChunk(${chunk.id})">Retry Chunk</button>
            </div>
            <h4>File: ${escapeHtml(chunk.fileId)}</h4>
            <p>Position: ${chunk.startPosition} - ${chunk.endPosition}</p>
            <details>
                <summary>Original Text</summary>
                <pre>${escapeHtml(chunk.content)}</pre>
            </details>
            ${chunk.extraContext ? `
                <details>
                    <summary>Extra Context</summary>
                    <pre>${escapeHtml(chunk.extraContext)}</pre>
                </details>
            ` : ''}
        </div>
    `;
}

// Helper function to render flash cards
function renderFlashCards(flashCards) {
    if (!flashCards || flashCards.length === 0) {
        return '<p>No flash cards available for this chunk.</p>';
    }

    return flashCards.map(card => `
        <div class="flash-card">
            <h4>Question:</h4>
            <p>${escapeHtml(card.question)}</p>
            <h4>Answer:</h4>
            <p>${escapeHtml(card.answer)}</p>
            <button class="btn btn-danger btn-sm" onclick="deleteFlashCard(${card.id})">Delete</button>
        </div>
    `).join('');
}

// Delete a specific flash card
function deleteFlashCard(flashCardId) {
    fetch(`Home/DeleteFlashCard?flashCardId=${flashCardId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                // Get the current chunk ID from the modal content
                const chunkInfoDiv = document.querySelector('.chunk-info');
                const chunkId = chunkInfoDiv.querySelector('.chunk-actions button').onclick.toString().match(/deleteAllFlashCards\((\d+)\)/)[1];

                // Refresh the flash cards view
                showFlashCardDetails(parseInt(chunkId));

                // Also refresh the chart to update counts
                loadFlashCardChart();
            } else {
                alert('Failed to delete flash card. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error deleting flash card:', error);
            alert('Failed to delete flash card. Please try again.');
        });
}

// Delete all flash cards for a specific chunk
function deleteAllFlashCards(chunkId) {
    fetch(`Home/DeleteAllFlashCards?chunkId=${chunkId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                // Close modal and refresh chart
                document.getElementById('flashCardModal').style.display = 'none';
                loadFlashCardChart();
            } else {
                alert('Failed to delete flash cards. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error deleting flash cards:', error);
            alert('Failed to delete flash cards. Please try again.');
        });
}

// Retry processing a specific chunk
function retryChunk(chunkId) {
    fetch(`Home/RetryChunk?chunkId=${chunkId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                // Close modal and refresh chart
                document.getElementById('flashCardModal').style.display = 'none';
                loadFlashCardChart();
            } else {
                alert('Failed to retry chunk. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error retrying chunk:', error);
            alert('Failed to retry chunk. Please try again.');
        });
}