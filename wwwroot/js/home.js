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

let chunks = new Map(); // Convert from array to Map for easier lookup

function loadFlashCardChart() {
    fetch('Home/GetFlashCardChartData')
        .then(response => response.json())
        .then(data => {
            // Initialize Map with data from server
            chunks.clear();
            data.forEach(chunk => {
                chunks.set(chunk.chunkId, {
                    ...chunk,
                    element: null, // Will store reference to DOM element
                    isDeleted: false,
                });
            });
            renderFlashCardChart();
        })
        .catch(error => {
            console.error('Error loading chart data:', error);
            document.getElementById('flashCardChart').innerHTML =
                `<div class="alert alert-danger">Failed to load chart data: ${error.message}</div>`;
        });
}

function renderFlashCardChart() {
    const chartContainer = document.getElementById('flashCardChart');
    chartContainer.innerHTML = ''; // Clear previous chart

    if (chunks.size === 0) {
        chartContainer.innerHTML = '<div class="alert alert-info">No flash card data available</div>';
        return;
    }

    // Group data by FileId for better organization
    const fileGroups = {};
    for (const [_, chunk] of chunks) {
        if (!fileGroups[chunk.fileId]) {
            fileGroups[chunk.fileId] = [];
        }
        fileGroups[chunk.fileId].push(chunk);
    }

    // Get non-zero flash card counts to calculate percentiles
    const nonZeroCounts = Array.from(chunks.values())
        .filter(chunk => !chunk.isDeleted) // Exclude deleted chunks
        .map(chunk => chunk.flashCardCount)
        .filter(count => count > 0);

    // Calculate percentile thresholds for the 5 intensity buckets
    const percentiles = calculatePercentiles(nonZeroCounts);

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

            if (chunk.isDeleted) {
                box.classList.add('deleted');
            }

            // Calculate color intensity based on flash card count (0-5 scale)
            const intensity = getIntensityLevel(chunk.flashCardCount, percentiles);
            box.classList.add(`intensity-${intensity}`);

            // Store intensity level in chunk data for later use
            chunk.currentIntensity = intensity; //TODON'T. Why?

            // Add tooltip with details
            box.title = `Chunk ID: ${chunk.chunkId}\nFlash Cards: ${chunk.flashCardCount}`;

            // Add click event to show the modal with flash card details
            box.addEventListener('click', (event) => {
                // Highlight the last one you clicked so you can keep track if you're reviewing them in order to delete bad cards
                document.querySelector(".chart-box.last")?.classList.remove("last");
                event.target.classList.add("last");
                showFlashCardDetails(chunk.chunkId);
            });

            // Store reference to DOM element
            chunk.element = box;

            chartGrid.appendChild(box);
        });

        fileSection.appendChild(chartGrid);
        chartContainer.appendChild(fileSection);
    });
}

// Calculate percentiles for an array of values
function calculatePercentiles(values) {
    if (values.length === 0) return [];

    // Sort values in ascending order
    const sortedValues = [...values].sort((a, b) => a - b);

    // Calculate percentiles at 20%, 40%, 60%, 80% for 5 intensity levels
    const percentiles = [];
    for (let i = 1; i <= 4; i++) {
        const index = Math.floor(sortedValues.length * (i * 0.2)) - 1;
        percentiles.push(sortedValues[Math.max(0, index)]);
    }

    return percentiles;
}

// Determine intensity level based on percentiles
function getIntensityLevel(value, percentiles) {
    // Special case: Always use intensity-0 for zero values
    if (value === 0) return 0;

    // For non-zero values, determine which percentile bucket they fall into
    for (let i = 0; i < percentiles.length; i++) {
        if (value <= percentiles[i]) {
            return i + 1; // Intensity 1-4
        }
    }

    return 5; // Highest intensity bucket
}

function showFlashCardDetails(chunkId) {
    // Fetch flash card details from the server
    fetch(`Home/GetFlashCardDetails?chunkId=${chunkId}`)
        .then(response => response.json())
        .then(data => {
            const chunk = chunks.get(chunkId);

            // Update the chunk data with the latest from server
            if (chunk && data.chunk) {
                Object.assign(chunk, data.chunk);
                chunk.flashCards = data.flashCards;
            }

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
                        <button class="modal-close" onclick="closeChunkModal()">&times;</button>
                    </div>
                    ${renderChunkInfo(chunk)}
                    <div class="flash-cards-container">
                        ${renderFlashCards(chunk.flashCards, chunk)}
                    </div>
                </div>
            `;

            // Show the modal
            modal.style.display = 'block';

            // Set up editing functionality
            setupFlashCardEditing();

            // Add event to close modal when clicking outside
            modal.addEventListener('click', function (event) {
                if (event.target === modal) {
                    closeChunkModal();
                }
            });
        })
        .catch(error => {
            console.error('Error loading flash card details:', error);
            alert('Failed to load flash card details. Please try again.');
        });
}

function closeChunkModal() {
    document.getElementById('flashCardModal').style.display = 'none';
}

function setupFlashCardEditing() {
    const flashCardContents = document.querySelectorAll('.flash-card-content');

    flashCardContents.forEach(element => {
        // Store original content for cancel operation
        const flashCardElement = element.closest('.flash-card');
        const questionEl = flashCardElement.querySelector('.question');
        const answerEl = flashCardElement.querySelector('.answer');
        const flashCardId = parseInt(flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/)?.[1]);

        // Skip if we can't find the flashcard ID
        if (!flashCardId) return;

        // Find the chunk that contains this flash card
        for (const [chunkId, chunk] of chunks.entries()) {
            if (!chunk.flashCards) continue;

            const flashCard = chunk.flashCards.find(card => card.id === flashCardId);
            if (flashCard) {
                // Initialize editing state if not already present
                if (!flashCard.editState) {
                    flashCard.editState = {
                        originalQuestion: flashCard.question,
                        originalAnswer: flashCard.answer,
                        isEditing: false,
                        isModified: false
                    };
                }
                break;
            }
        }

        // Set up focus event - mark the element as being edited
        element.addEventListener('focus', function () {
            const cardId = parseInt(flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/)?.[1]);
            if (!cardId) return;

            // Find the flash card in chunks
            let targetCard = null;
            let parentChunk = null;

            for (const [chunkId, chunk] of chunks.entries()) {
                if (!chunk.flashCards) continue;

                targetCard = chunk.flashCards.find(card => card.id === cardId);
                if (targetCard) {
                    parentChunk = chunk;
                    break;
                }
            }

            if (targetCard) {
                targetCard.editState.isEditing = true;
            }
        });

        // Set up input event to track modifications
        element.addEventListener('input', function () {
            const cardId = parseInt(flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/)?.[1]);
            if (!cardId) return;

            // Find the flash card in chunks
            for (const [chunkId, chunk] of chunks.entries()) {
                if (!chunk.flashCards) continue;

                const targetCard = chunk.flashCards.find(card => card.id === cardId);
                if (targetCard) {
                    targetCard.editState.isModified = true;
                    element.classList.add('modified');
                    break;
                }
            }
        });

        // Set up blur event to highlight modified fields
        element.addEventListener('blur', function () {
            const cardId = parseInt(flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/)?.[1]);
            if (!cardId) return;

            // Find the flash card in chunks
            for (const [chunkId, chunk] of chunks.entries()) {
                if (!chunk.flashCards) continue;

                const targetCard = chunk.flashCards.find(card => card.id === cardId);
                if (targetCard && targetCard.editState) {
                    // If it's modified but not explicitly saved or canceled
                    if (targetCard.editState.isModified) {
                        element.classList.add('modified');
                    }

                    targetCard.editState.isEditing = false;
                    break;
                }
            }
        });

        // Handle keyboard events
        element.addEventListener('keydown', function (event) {
            const cardId = parseInt(flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/)?.[1]);
            if (!cardId) return;

            // Find the flash card and its parent chunk
            let targetCard = null;

            for (const [_, chunk] of chunks.entries()) {
                if (!chunk.flashCards) continue;

                targetCard = chunk.flashCards.find(card => card.id === cardId);
                if (targetCard) {
                    break;
                }
            }

            if (!targetCard) return;

            // Handle Escape key - cancel editing
            if (event.key === 'Escape') {
                event.preventDefault();

                // Reset content to original
                if (element.classList.contains('question')) {
                    element.textContent = targetCard.editState.originalQuestion;
                } else if (element.classList.contains('answer')) {
                    element.textContent = targetCard.editState.originalAnswer;
                }

                // Reset edit state
                element.classList.remove('modified');
                targetCard.editState.isModified = false;
                targetCard.editState.isEditing = false;

                // Remove focus
                element.blur();
            }

            // Handle Enter key (without Shift) - save changes
            else if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();

                // Get current question and answer values
                const newQuestion = questionEl.textContent;
                const newAnswer = answerEl.textContent;

                // Update flash card on the server
                updateFlashCardContent(cardId, newQuestion, newAnswer).then(success => {
                    if (success) {
                        // Update the flash card data
                        targetCard.question = newQuestion;
                        targetCard.answer = newAnswer;

                        // Update edit state
                        targetCard.editState.originalQuestion = newQuestion;
                        targetCard.editState.originalAnswer = newAnswer;
                        targetCard.editState.isModified = false;

                        // Remove modified class
                        questionEl.classList.remove('modified');
                        answerEl.classList.remove('modified');
                    }
                });

                // Remove focus
                element.blur();
            }

            // Handle Shift+Enter - allow newline
            else if (event.key === 'Enter' && event.shiftKey) {
                // Default behavior (insert newline) is allowed
            }
        });
    });
}

function cancelEditing(flashCardId) {
    // Get all content-editable elements
    const editableElements = document.querySelectorAll('.flash-card-content');

    editableElements.forEach(element => {
        const flashCardElement = element.closest('.flash-card');
        if (!flashCardElement) return;

        // Get the flash card ID from the delete button's onclick attribute
        const cardIdMatch = flashCardElement.querySelector('button')?.onclick.toString().match(/deleteFlashCard\((\d+)/);
        const cardId = cardIdMatch ? parseInt(cardIdMatch[1]) : null;

        // If flashCardId is provided, only cancel editing for that specific card
        if (flashCardId && cardId !== flashCardId) return;

        // Find the flash card in chunks
        for (const [_, chunk] of chunks.entries()) {
            if (!chunk.flashCards) continue;

            const targetCard = chunk.flashCards.find(card => card.id === cardId);
            if (targetCard && targetCard.editState) {
                // Reset content to original state
                if (targetCard.editState.isModified) {
                    if (element.classList.contains('question')) {
                        element.textContent = targetCard.editState.originalQuestion;
                    } else if (element.classList.contains('answer')) {
                        element.textContent = targetCard.editState.originalAnswer;
                    }
                }

                // Reset edit state
                element.classList.remove('modified');
                targetCard.editState.isModified = false;
                targetCard.editState.isEditing = false;
                break;
            }
        }
    });
}

// Function to update flash card content on the server
function updateFlashCardContent(flashCardId, question, answer) {
    return fetch('Home/UpdateFlashCard', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            id: flashCardId,
            question: question,
            answer: answer
        })
    })
        .then(response => {
            if (response.ok) {
                return true;
            } else {
                alert('Failed to update flash card. Please try again.');
                return false;
            }
        })
        .catch(error => {
            console.error('Error updating flash card:', error);
            alert('Failed to update flash card. Please try again.');
            return false;
        });
}

// Helper function to render chunk info
function renderChunkInfo(chunk) {
    if (!chunk) {
        return '<div class="chunk-info">No chunk data available</div>';
    }

    const storedChunk = chunks.get(parseInt(chunk.id));
    const isDeleted = storedChunk && storedChunk.isDeleted;

    return `
        <div class="chunk-info">
            <div class="chunk-actions">
                ${!isDeleted ?
                    `<button class="btn btn-danger btn-sm" onclick="deleteChunk(${chunk.id})">Delete Chunk</button>
                     <button class="btn btn-primary btn-sm" onclick="retryChunk(${chunk.id})">Retry Chunk</button>` :
                    `<button class="btn btn-success btn-sm" onclick="restoreChunk(${chunk.id})">Restore Chunk</button>
                     <button class="btn btn-primary btn-sm" onclick="resubmitChunk(${chunk.id})">Resubmit Chunk</button>`
                }
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
function renderFlashCards(flashCards, chunk) {
    if (!flashCards || flashCards.length === 0) {
        return '<p>No flash cards available for this chunk.</p>';
    }

    const isDeleted = chunk && chunk.isDeleted;

    return flashCards.map(card => `
        <div class="flash-card">
            <h4>Question:</h4>
            <p class="flash-card-content question" contenteditable="true">${escapeHtml(card.question)}</p>
            <h4>Answer:</h4>
            <p class="flash-card-content answer" contenteditable="true">${escapeHtml(card.answer)}</p>
            ${!isDeleted ?
            `<button class="btn btn-danger btn-sm" onclick="deleteFlashCard(${card.id}, ${chunk.chunkId})">Delete</button>` :
            ''}
        </div>
    `).join('');
}

// Delete a specific flash card
function deleteFlashCard(flashCardId, chunkId) {
    cancelEditing(flashCardId);
    fetch(`Home/DeleteFlashCard?flashCardId=${flashCardId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                // Get chunk from our stored Map
                const chunk = chunks.get(chunkId);
                if (chunk) {
                    // Decrease flash card count
                    chunk.flashCardCount--;

                    // Recalculate intensity if needed
                    if (chunk.element) {
                        // Get all non-zero flash card counts for percentiles
                        const nonZeroCounts = Array.from(chunks.values())
                            .filter(c => !c.isDeleted && c.flashCardCount > 0)
                            .map(c => c.flashCardCount);

                        const percentiles = calculatePercentiles(nonZeroCounts);
                        const newIntensity = getIntensityLevel(chunk.flashCardCount, percentiles);

                        // Update intensity class if it changed
                        if (newIntensity !== chunk.currentIntensity) {
                            chunk.element.classList.remove(`intensity-${chunk.currentIntensity}`);
                            chunk.element.classList.add(`intensity-${newIntensity}`);
                            chunk.currentIntensity = newIntensity;
                        }

                        // Update tooltip
                        chunk.element.title = `Chunk ID: ${chunk.chunkId}\nFlash Cards: ${chunk.flashCardCount}`;
                    }
                }

                // Refresh the flash cards view
                showFlashCardDetails(chunkId);
            } else {
                alert('Failed to delete flash card. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error deleting flash card:', error);
            alert('Failed to delete flash card. Please try again.');
        });
}

// Delete a chunk and all its flash cards
function deleteChunk(chunkId) {
    cancelEditing();
    fetch(`Home/DeleteChunk?chunkId=${chunkId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                // Mark chunk as deleted in our Map
                const chunk = chunks.get(chunkId);
                if (chunk) {
                    chunk.isDeleted = true;

                    // Mark the corresponding chart-box as deleted
                    if (chunk.element) {
                        chunk.element.classList.add('deleted');
                    }
                }

                closeChunkModal();
            } else {
                alert('Failed to delete chunk. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error deleting chunk:', error);
            alert('Failed to delete chunk. Please try again.');
        });
}

// Retry processing a specific chunk
function retryChunk(chunkId) {
    fetch(`Home/RetryChunk?chunkId=${chunkId}`, {
        method: 'POST'
    })
        .then(response => {
            if (response.ok) {
                closeChunkModal();
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

// Restore a deleted chunk
function restoreChunk(chunkId) {
    // Get the chunk from our local chunks map
    const chunk = chunks.get(chunkId);
    if (!chunk) {
        alert('Chunk data not found. Must be a bug!');
        return;
    }

    // Recreate a FlashCardDetails object in the format the API expects
    const flashCardDetails = {
        chunk: {
            id: chunk.id || chunk.chunkId,
            content: chunk.content,
            startPosition: chunk.startPosition,
            endPosition: chunk.endPosition,
            fileId: chunk.fileId,
            extraContext: chunk.extraContext
        },
        flashCards: chunk.flashCards || []
    };

    // Send the data to the API
    fetch('Home/RestoreChunk', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(flashCardDetails)
    })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                // Mark chunk as not deleted in our Map
                chunk.isDeleted = false;

                // Update the chunk with new ID from server
                const newChunkId = data.chunkId;

                // The record IDs will have changed, so we need to update our data structures
                // Add the chunk with the new ID to our map
                chunks.set(newChunkId, {
                    ...chunk,
                    chunkId: newChunkId,
                    id: newChunkId
                });
                chunks.delete(chunkId);
                chunkId = newChunkId;

                // Update the flash cards with their new IDs
                if (data.flashCards && chunk.flashCards) { //Hopefully unnecessary check
                    data.flashCards.forEach((newCardId, index) => {
                        if (index < chunk.flashCards.length) { //Hopefully unnecessary check
                            chunk.flashCards[index].id = newCardId;
                            chunk.flashCards[index].originId = newChunkId;
                        }
                    });
                }

                // Update the flash card count from server response
                chunk.flashCardCount = data.flashCardCount;

                // Update UI
                if (chunk.element) {
                    chunk.element.classList.remove('deleted');
                    chunk.element.title = `Chunk ID: ${chunkId}\nFlash Cards: ${chunk.flashCardCount}`;

                    // Remove old event listener (now using the wrong chunk ID)
                    const oldElement = chunk.element;
                    const newElement = oldElement.cloneNode(true);
                    oldElement.parentNode.replaceChild(newElement, oldElement);

                    // Add new event listener with updated chunkId
                    newElement.addEventListener('click', () => showFlashCardDetails(chunkId));

                    // Update the element reference in our chunk object
                    chunk.element = newElement;
                }

                // Close modal and show it again with normal options (with updated chunkId)
                closeChunkModal();
                showFlashCardDetails(chunkId);
            } else {
                alert('Failed to restore chunk. Please try again.');
            }
        })
        .catch(error => {
            console.error('Error restoring chunk:', error);
            alert('Failed to restore chunk. Please try again.');
        });
}

// Resubmit a deleted chunk for processing
function resubmitChunk(chunkId) {
    // Get the chunk from our local chunks map
    const chunk = chunks.get(chunkId);
    if (!chunk) {
        alert('Chunk data not found. Must be a bug!');
        return;
    }

    // Create a FormData instance to match the format expected by UploadFile
    const formData = new FormData();

    // Create a text file from the chunk content with the original filename
    const file = new File([chunk.content], chunk.fileId, { type: 'text/plain' });
    formData.append('files', file);

    // Add extra context if present
    if (chunk.extraContext) {
        formData.append('extraContext', chunk.extraContext);
    }

    // Set stripHtml to false by default
    formData.append('stripHtml', false);

    // Show upload status
    closeChunkModal();

    const uploadStatus = document.getElementById('upload-status');
    uploadStatus.innerHTML = '<div class="alert alert-info">Re-uploading chunk...</div>';

    // Use the same upload mechanism as the form
    fetch('Home/UploadFile', {
        method: 'POST',
        body: formData
    })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                uploadStatus.innerHTML = `<div class="alert alert-success">${data.message}</div>`;
                // No need to delete the chunk as it's already marked as deleted
            } else {
                uploadStatus.innerHTML = `<div class="alert alert-danger">${data.message}</div>`;
            }
        })
        .catch(error => {
            console.error('Error resubmitting chunk:', error);
            uploadStatus.innerHTML = `<div class="alert alert-danger">Upload failed: ${error.message}</div>`;
        });
}