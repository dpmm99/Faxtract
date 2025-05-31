namespace Faxtract.Models;

public class FlashCard
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public required TextChunk Origin { get; set; }

    public static IEnumerable<FlashCard> ParseFromText(string text, TextChunk origin)
    {
        var lines = text.Split('\n');
        int currentIndex = 0;

        while (currentIndex < lines.Length)
        {
            // Find the next Q: marker
            int questionIndex = -1;
            for (int i = currentIndex; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].TrimStart();
                if (trimmedLine.StartsWith("Q:"))
                {
                    questionIndex = i;
                    break;
                }
            }

            // If no Q: marker is found, we're done
            if (questionIndex == -1)
            {
                break;
            }

            // Find the next A: marker after the Q:
            int answerIndex = -1;
            for (int i = questionIndex + 1; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].TrimStart();
                if (trimmedLine.StartsWith("A:"))
                {
                    answerIndex = i;
                    break;
                }
                else if (trimmedLine.StartsWith("Q:"))
                {
                    // Found another Q: before an A:, so this Q: is invalid
                    break;
                }
            }

            // If we found a valid Q:/A: pair
            if (answerIndex != -1)
            {
                // Extract question (everything from Q: line up to but not including A: line)
                var question = string.Join("\n",
                    lines.Skip(questionIndex).Take(answerIndex - questionIndex))
                    .TrimStart("Q:".ToCharArray()).Trim();

                // Find the next Q: marker after the A: (if any)
                int nextQuestionIndex = -1;
                for (int i = answerIndex + 1; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("Q:"))
                    {
                        nextQuestionIndex = i;
                        break;
                    }
                }

                // Extract answer (everything from A: line up to the next Q: or end of text)
                int answerLineCount = nextQuestionIndex == -1
                    ? lines.Length - answerIndex
                    : nextQuestionIndex - answerIndex;

                var answer = string.Join("\n", lines.Skip(answerIndex).Take(answerLineCount))
                    .TrimStart("A:".ToCharArray()).Trim();

                yield return new FlashCard
                {
                    Question = question,
                    Answer = answer,
                    Origin = origin
                };

                // Move to the next question or end
                currentIndex = nextQuestionIndex == -1 ? lines.Length : nextQuestionIndex;
            }
            else
            {
                // This Q: doesn't have a matching A:, move past it
                currentIndex = questionIndex + 1;
            }
        }
    }
}
