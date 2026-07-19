# specc
**A spec-driven compiler experiment.**

Why bother writing source code at all in 2026?

I've built so many LLM-driven projects this year.  As my own techniques have improved, they have converged on a particular pipeline:
* Spec-driven development, e.g. write a specification document before writing any code.
* The specification should include a complete description and acceptance criteria for a feature.
* The LLM writes the code based on the description, then it writes the tests based on the acceptance criteria.
* Continue iterating until the unit tests pass.

The source code is then written in C++.  Or C#.  Or Ruby.  PHP.  Rust.  Whatever.  The source code is becoming about as relevant as the assembly language output these high-level compilers used to surface for us.

The lesson here is that the new language coming forward is not another iteration of object-oriented programming, but rather the well-written specification.  The requirements document.  A good spec produces a good product.  A bad spec produces AI slop.

So why bother writing the source code at all?  Why waste the LLM's neural space learning to read human, then also learning to read a human representation for machine code?

If you've ever written a compiler, you're familiar with a particular process:
* The human input is tokenized.
* The tokens are arranged into an abstract syntax tree (AST).
* The AST is walked to produce some kind of intermediary byte-code.
* If you're particularly adventurous, that byte-code might be converted to machine code.

What I'm proposing here is that the LLM can skip the source code and go straight to its own favored implementation of some kind of AST.

That's the seed of Specc.  You give it a Markdown description of a program.  It produces a native executable.  No source code in between.

Here's a concrete example.  This is the entire input for a working guessing game:

```markdown
# GuessingGame

Write a program named GuessingGame.

Pick a random number.

Continue running these steps until the user guesses the correct number:
1. Ask the user to guess the number.
2. If the user guesses the correct number then tell they guessed correctly and exit.
3. If the user's guess is too low, tell them it's too low.
4. If the user's guess is too high, tell them it's too high.
```

Run `specc GuessingGame.md` and you get a native binary.  That's it.  Specc's first pass extracts this structured spec from the prose:

```yaml
program: GuessingGame

random:
  name: target
  min: 1
  max: 100

print: "Guess a number between 1 and 100."

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
```

Every pass after this is deterministic.  The spec becomes a semantic graph.  The graph becomes a control flow graph.  The CFG becomes stack IR.  The stack IR becomes .NET IL.  The IL becomes a native binary.  Two models participate along the way, but neither one is driving.

## Two models, two jobs

Here's the amazing part: the LLM doesn't actually write the program.  I was surprised at this outcome as I worked through the problem.  It's the assumption I began this experiment with.

Specc uses two models.  The first is a small language model (e.g. ministral-3b, qwen2.5-coder:3b-instruct) that does one thing: read your prose and extract the structured spec you saw above.  That's its entire role.  It doesn't touch the graph, the CFG, the IL, or the binary.

The second is an embedding model (mxbai-embed-large).  After the semantic graph is built, it runs once per node and attaches a vector to each one - a point in high-dimensional space that encodes what that node *means*.  A loop node and a while node end up close together.  A branch node and a print node don't.  The embeddings are metadata on the graph; they don't change its structure or drive any control flow decisions.

Everything else - building the graph, lowering it to a control flow graph, emitting IL, verifying the output - is deterministic code.

This matters because most "LLM compiler" projects I've seen are just code generators with extra steps.  The LLM writes Python, or the LLM writes assembly, and then you run it.  You're still trusting the model's judgment on every line.  Specc is trying to do something different: treat the LLM as a single optical pass - a lens that converts human language into structured meaning - and then hand off to a compiler that doesn't hallucinate.

## The pipeline

The intermediate representation is a semantic graph.  Nodes represent program concepts: loops, branches, variables, output statements.  Edges represent relationships: control flow, data dependencies, assertions about expected output.  The graph is deterministically lowered to a stack-based IR, then to .NET IL, then assembled into a native binary using the .NET runtime's own apphost mechanism - no shell-outs, no intermediate files you didn't ask for.

The acceptance circuit closes the loop.  Before the spec is extracted, Specc also asks the LLM: given this prose description, what should the program output?  Those authorial assertions are stored separately from the graph.  After the binary is built, it's run, and its stdout is diffed against those assertions.  If it passes, the compilation is stored in a graph repository.  If it fails, the compilation is discarded.  The repository only contains verified programs.

An interesting side-effect of this repository: because it stores the raw spec text for each verified compilation, it acts like a standard library for future extractions.  When you compile a new program, Specc finds prior compilations that used similar constructs and injects their specs into the extraction prompt.  The LLM gets to pattern-match against verified examples instead of synthesizing from rules alone.  This turned out to be the difference between reliable extraction and garbage.  The compiler *learns* as it successfully generates ever more complex solutions.

## What's next

This brings a new experiment to the forefront: if programs are graphs with semantic embeddings, two programs that implement the same intent should cluster together.  You should be able to search a repository of compiled programs by meaning.  You should be able to ask whether a new spec is structurally similar to something you've already built, or whether it requires new machinery.  Graph structure might even be refined by gradient - nudging the embedding space so that semantically equivalent programs converge, and semantically distinct ones don't.

I don't know yet whether any of that will work.  But the pipeline compiles FizzBuzz, Fibonacci, BubbleSort, a guessing game with random numbers and interactive input, and a Collatz sequence runner.  It is technically "Turing complete" (quotes intended, the 3B model couldn't get there on its own).  The spec-to-binary path is solid.  The interesting questions are now about what you can do with the graph.

The code is on [GitHub](https://github.com/treytomes/specc).  Future articles will go deeper on specific passes - the semantic graph, the acceptance circuit, and the embedding geometry experiments.
