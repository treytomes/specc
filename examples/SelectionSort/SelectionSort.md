# SelectionSort

Write a program called SelectionSort that sorts an array of 8 integers in place
using the selection sort algorithm.

Start with the array: 64 25 12 22 11 90 3 45

Use two nested loops. The outer loop runs from index 0 to 6 (inclusive).
The inner loop runs from index 1 plus the outer loop index to 7 (inclusive).

For each inner iteration, if the element at the current inner index is less than
the element at min_index, update min_index to the current inner index.
At the end of each outer iteration, swap the elements at positions i and min_index.

After sorting, print each element on its own line.

## Expected Output

After sorting, the program should print these 8 lines in order:

```
3
11
12
22
25
45
64
90
```
