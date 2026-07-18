program: SimpleGuess

variable:
  name: target
  type: int
  initial_value: 42

print: "Guess a number between 1 and 100:"

while:
  compare_lhs: {guess}
  compare: ne
  compare_rhs: {target}

variable:
  name: guess
  type: int
  source: stdin

branch:
  condition: too_low
  compare: lt
  compare_with: {target}
  true_output: "Too low!"

branch:
  condition: too_high
  compare: gt
  compare_with: {target}
  true_output: "Too high!"

branch:
  condition: correct
  compare: eq
  compare_with: {target}
  true_output: "Correct!"
